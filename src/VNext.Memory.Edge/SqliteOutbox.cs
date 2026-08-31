using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VNext.Memory.Domain;

namespace VNext.Memory.Edge;

public sealed record OutboxItem(
    Guid Id,
    SignedEvidenceEnvelope Envelope,
    int Attempts);

public sealed class SqliteOutbox(
    IOptions<EdgeOptions> options,
    ILogger<SqliteOutbox> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly EdgeOptions _options = options.Value;
    private readonly string _connectionString = BuildConnectionString(options.Value.OutboxPath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 5000;

                CREATE TABLE IF NOT EXISTS outbox_events (
                    id TEXT PRIMARY KEY,
                    payload TEXT NOT NULL,
                    actor_id TEXT NOT NULL,
                    session_id TEXT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    next_attempt_at INTEGER NOT NULL,
                    last_error TEXT NULL,
                    created_at INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_outbox_due
                    ON outbox_events (next_attempt_at, created_at);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task EnqueueAsync(
        SignedEvidenceEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO outbox_events (
                    id, payload, actor_id, session_id, attempts,
                    next_attempt_at, created_at)
                VALUES (
                    $id, $payload, $actorId, $sessionId, 0,
                    $nextAttemptAt, $createdAt)
                ON CONFLICT(id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$id", envelope.EventId.ToString("D"));
            command.Parameters.AddWithValue(
                "$payload",
                JsonSerializer.Serialize(envelope, JsonOptions));
            command.Parameters.AddWithValue(
                "$actorId",
                string.IsNullOrWhiteSpace(envelope.Observation.AgentId)
                    ? _options.DefaultActorId
                    : envelope.Observation.AgentId);
            command.Parameters.AddWithValue(
                "$sessionId",
                (object?)envelope.Observation.SessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$nextAttemptAt", now);
            command.Parameters.AddWithValue("$createdAt", now);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<OutboxItem>> GetDueAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, payload, actor_id, session_id, attempts
            FROM outbox_events
            WHERE next_attempt_at <= $now
            ORDER BY created_at
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var items = new List<OutboxItem>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = Guid.Parse(reader.GetString(0));
            var payload = reader.GetString(1);
            var envelope = DeserializeEnvelope(payload);

            if (envelope is null)
            {
                var legacyRequest = DeserializeLegacyRequest(payload);
                if (legacyRequest is not null)
                {
                    envelope = EvidenceEnvelopeCryptography.SignEvidence(
                        legacyRequest,
                        EvidenceChannel.AgentObservation,
                        "vnext-memory-edge-legacy-outbox",
                        _options.CoreToken,
                        adapterVersion: "0.2.0",
                        eventId: id);
                }
            }

            if (envelope is null)
            {
                logger.LogWarning(
                    "Ignoring unreadable outbox item {OutboxId}.",
                    id);
                continue;
            }

            items.Add(new OutboxItem(
                id,
                envelope,
                reader.GetInt32(4)));
        }

        return items;
    }

    public async Task MarkSucceededAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        await ExecuteWriteAsync(
            "DELETE FROM outbox_events WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", id.ToString("D")),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(
        Guid id,
        int attempts,
        string error,
        CancellationToken cancellationToken)
    {
        var cappedAttempts = Math.Clamp(attempts, 1, 20);
        var backoffSeconds = Math.Min(300, Math.Pow(2, cappedAttempts));
        var nextAttempt = DateTimeOffset.UtcNow
            .AddSeconds(backoffSeconds)
            .ToUnixTimeMilliseconds();

        await ExecuteWriteAsync(
            """
            UPDATE outbox_events
            SET attempts = $attempts,
                next_attempt_at = $nextAttempt,
                last_error = $error
            WHERE id = $id;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                command.Parameters.AddWithValue("$attempts", attempts);
                command.Parameters.AddWithValue("$nextAttempt", nextAttempt);
                command.Parameters.AddWithValue(
                    "$error",
                    error.Length > 1_000 ? error[..1_000] : error);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteWriteAsync(
        string sql,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = sql;
            configure(command);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static SignedEvidenceEnvelope? DeserializeEnvelope(string payload)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<SignedEvidenceEnvelope>(
                payload,
                JsonOptions);
            if (envelope is null ||
                envelope.EventId == Guid.Empty ||
                envelope.Observation is null)
            {
                return null;
            }

            return envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MemoryRecordRequest? DeserializeLegacyRequest(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<MemoryRecordRequest>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildConnectionString(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("Invalid outbox path."));

        return new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }
}
