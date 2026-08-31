using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using VNext.Memory.Domain;

namespace VNext.Memory.Infrastructure.Postgres;

public sealed class PostgresRetrievalTelemetryStore(
    NpgsqlDataSource dataSource) : IRetrievalTelemetryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task RecordTraceAsync(
        RetrievalTrace trace,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO retrieval_traces (
                id, tenant_id, principal_id, device_id, actor_id,
                session_id, query, scope, created_at)
            VALUES (
                @Id, @TenantId, @PrincipalId, @DeviceId, @ActorId,
                @SessionId, @Query, CAST(@Scope AS jsonb), @CreatedAt)
            ON CONFLICT (id) DO NOTHING;
            """,
            new
            {
                trace.Id,
                trace.Identity.TenantId,
                trace.Identity.PrincipalId,
                trace.Identity.DeviceId,
                trace.Identity.ActorId,
                trace.Identity.SessionId,
                trace.Query,
                Scope = JsonSerializer.Serialize(trace.Scope, JsonOptions),
                trace.CreatedAt
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var item in trace.Items)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO retrieval_trace_items (
                    trace_id, claim_id, rank, relevance)
                VALUES (@TraceId, @ClaimId, @Rank, @Relevance)
                ON CONFLICT (trace_id, claim_id) DO NOTHING;
                """,
                new
                {
                    TraceId = trace.Id,
                    item.ClaimId,
                    item.Rank,
                    item.Relevance
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RetrievalFeedbackResult> ApplyFeedbackAsync(
        RetrievalFeedbackRequest request,
        RequestIdentity identity,
        SourceAssurance assurance,
        CancellationToken cancellationToken)
    {
        var feedbackId = assurance.EventId ?? Guid.NewGuid();
        var reasons = assurance.ReasonCodes.ToList();

        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var replayed = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM retrieval_feedback WHERE id = @Id);",
                new { Id = feedbackId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (replayed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RetrievalFeedbackResult(
                request.TraceId,
                request.Outcome,
                0,
                Accepted: true,
                Authoritative: IsAuthoritative(identity, assurance),
                [.. reasons, "FEEDBACK_REPLAYED"],
                Replayed: true);
        }

        var traceOwner = await connection.QuerySingleOrDefaultAsync<TraceOwnerRow>(
            new CommandDefinition(
                """
                SELECT
                    tenant_id AS TenantId,
                    principal_id AS PrincipalId
                FROM retrieval_traces
                WHERE id = @TraceId;
                """,
                new { request.TraceId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (traceOwner is null ||
            !string.Equals(traceOwner.TenantId, identity.TenantId, StringComparison.Ordinal) ||
            (!identity.IsAdministrator &&
             !string.Equals(
                 traceOwner.PrincipalId,
                 identity.PrincipalId,
                 StringComparison.Ordinal)))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RetrievalFeedbackResult(
                request.TraceId,
                request.Outcome,
                0,
                Accepted: false,
                Authoritative: false,
                [.. reasons, "TRACE_NOT_FOUND_OR_NOT_OWNED"]);
        }

        var traceClaimIds = (await connection.QueryAsync<Guid>(
            new CommandDefinition(
                """
                SELECT claim_id
                FROM retrieval_trace_items
                WHERE trace_id = @TraceId;
                """,
                new { request.TraceId },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToHashSet();
        var requestedIds = request.ClaimIds is { Count: > 0 }
            ? request.ClaimIds.Where(traceClaimIds.Contains).Distinct().ToArray()
            : traceClaimIds.ToArray();
        var authoritative = IsAuthoritative(identity, assurance);

        reasons.Add(authoritative
            ? "AUTHORITATIVE_FEEDBACK"
            : "NON_AUTHORITATIVE_FEEDBACK_RECORDED");

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO retrieval_feedback (
                id, trace_id, tenant_id, principal_id, device_id, actor_id,
                outcome, detail, channel, assurance_level, effective_trust,
                authoritative, reason_codes, created_at)
            VALUES (
                @Id, @TraceId, @TenantId, @PrincipalId, @DeviceId, @ActorId,
                @Outcome, @Detail, @Channel, @AssuranceLevel, @EffectiveTrust,
                @Authoritative, CAST(@ReasonCodes AS jsonb), @CreatedAt);
            """,
            new
            {
                Id = feedbackId,
                request.TraceId,
                identity.TenantId,
                identity.PrincipalId,
                identity.DeviceId,
                identity.ActorId,
                Outcome = (short)request.Outcome,
                Detail = NormalizeDetail(request.Detail),
                Channel = (short)assurance.Channel,
                AssuranceLevel = (short)assurance.Level,
                EffectiveTrust = (short)assurance.EffectiveTrust,
                Authoritative = authoritative,
                ReasonCodes = JsonSerializer.Serialize(reasons, JsonOptions),
                CreatedAt = DateTimeOffset.UtcNow
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var affected = 0;
        var mayApplyHelpful = assurance.SignatureVerified &&
                              assurance.EffectiveTrust >= SourceTrust.UserExplicit;
        var shouldMutate = authoritative ||
                           (request.Outcome == RetrievalOutcome.Helpful &&
                            mayApplyHelpful);

        if (shouldMutate)
        {
            foreach (var claimId in requestedIds)
            {
                var claim = await connection.QuerySingleOrDefaultAsync<ClaimFeedbackRow>(
                    new CommandDefinition(
                        """
                        SELECT
                            id AS Id,
                            status AS Status,
                            confidence AS Confidence,
                            current_version AS CurrentVersion,
                            statement AS Statement,
                            scope::text AS ScopeJson
                        FROM memory_claims
                        WHERE id = @ClaimId
                          AND tenant_id = @TenantId
                        FOR UPDATE;
                        """,
                        new
                        {
                            ClaimId = claimId,
                            identity.TenantId
                        },
                        transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);

                if (claim is null)
                {
                    continue;
                }

                var (newStatus, newConfidence) = ApplyOutcome(
                    claim,
                    request.Outcome,
                    authoritative);

                if (newStatus == (MemoryStatus)claim.Status &&
                    Math.Abs(newConfidence - claim.Confidence) < 0.000001)
                {
                    continue;
                }

                var nextVersion = claim.CurrentVersion + 1;
                var now = DateTimeOffset.UtcNow;
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE memory_claims
                    SET status = @Status,
                        confidence = @Confidence,
                        current_version = @Version,
                        updated_at = @UpdatedAt
                    WHERE id = @ClaimId;

                    INSERT INTO memory_claim_versions (
                        claim_id, version, status, statement, scope,
                        confidence, reason, created_at)
                    VALUES (
                        @ClaimId, @Version, @Status, @Statement,
                        CAST(@Scope AS jsonb), @Confidence,
                        @Reason, @UpdatedAt);
                    """,
                    new
                    {
                        ClaimId = claim.Id,
                        Status = (short)newStatus,
                        Confidence = newConfidence,
                        Version = nextVersion,
                        claim.Statement,
                        Scope = claim.ScopeJson,
                        Reason = $"retrieval-feedback:{request.Outcome}",
                        UpdatedAt = now
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                affected++;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new RetrievalFeedbackResult(
            request.TraceId,
            request.Outcome,
            affected,
            Accepted: true,
            Authoritative: authoritative,
            reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static bool IsAuthoritative(
        RequestIdentity identity,
        SourceAssurance assurance) =>
        identity.IsAdministrator ||
        assurance.EffectiveTrust >= SourceTrust.HumanApproved ||
        assurance.EffectiveTrust == SourceTrust.UserCorrection ||
        (assurance.DeterministicEvidenceVerified &&
         assurance.EffectiveTrust is SourceTrust.ToolObserved or SourceTrust.CodeDerived);

    private static (MemoryStatus Status, double Confidence) ApplyOutcome(
        ClaimFeedbackRow claim,
        RetrievalOutcome outcome,
        bool authoritative)
    {
        var status = (MemoryStatus)claim.Status;
        var confidence = claim.Confidence;

        switch (outcome)
        {
            case RetrievalOutcome.Helpful:
                confidence += 0.01;
                break;

            case RetrievalOutcome.Confirmed when authoritative:
                confidence += 0.08;
                if (status == MemoryStatus.Probation)
                {
                    status = MemoryStatus.Active;
                }
                break;

            case RetrievalOutcome.Contradicted when authoritative:
                confidence -= 0.20;
                status = MemoryStatus.Stale;
                break;

            case RetrievalOutcome.Harmful when authoritative:
                confidence -= 0.40;
                status = MemoryStatus.Revoked;
                break;
        }

        return (status, Math.Clamp(confidence, 0.01, 1.0));
    }

    private static string? NormalizeDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var trimmed = detail.Trim();
        return trimmed.Length <= 2_000 ? trimmed : trimmed[..2_000];
    }

    private sealed class TraceOwnerRow
    {
        public string TenantId { get; init; } = string.Empty;
        public string PrincipalId { get; init; } = string.Empty;
    }

    private sealed class ClaimFeedbackRow
    {
        public Guid Id { get; init; }
        public short Status { get; init; }
        public double Confidence { get; init; }
        public int CurrentVersion { get; init; }
        public string Statement { get; init; } = string.Empty;
        public string ScopeJson { get; init; } = "{}";
    }
}
