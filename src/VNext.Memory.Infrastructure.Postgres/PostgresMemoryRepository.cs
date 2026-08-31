using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using VNext.Memory.Domain;

namespace VNext.Memory.Infrastructure.Postgres;

public sealed class PostgresMemoryRepository(
    NpgsqlDataSource dataSource) : IMemoryRepository, IDeviceIdentityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<PersistMemoryResult> PersistAsync(
        EvidenceEvent evidence,
        MemoryCandidate candidate,
        AdmissionDecision decision,
        string dedupeKey,
        string scopeHash,
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
            INSERT INTO evidence_events (
                id, tenant_id, principal_id, device_id, actor_id, session_id,
                source_type, source_reference, kind, trust, content, content_hash,
                scope, occurred_at, created_at)
            VALUES (
                @Id, @TenantId, @PrincipalId, @DeviceId, @ActorId, @SessionId,
                @SourceType, @SourceReference, @Kind, @Trust, @Content, @ContentHash,
                CAST(@Scope AS jsonb), @OccurredAt, @CreatedAt);
            """,
            new
            {
                evidence.Id,
                TenantId = evidence.Identity.TenantId,
                PrincipalId = evidence.Identity.PrincipalId,
                evidence.Identity.DeviceId,
                evidence.Identity.ActorId,
                evidence.Identity.SessionId,
                evidence.SourceType,
                evidence.SourceReference,
                Kind = (short)evidence.Kind,
                Trust = (short)evidence.Trust,
                evidence.Content,
                evidence.ContentHash,
                Scope = JsonSerializer.Serialize(evidence.Scope, JsonOptions),
                evidence.OccurredAt,
                evidence.CreatedAt
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO admission_decisions (
                id, evidence_id, candidate_id, disposition, score,
                reason_codes, required_actions, review_after, created_at)
            VALUES (
                @Id, @EvidenceId, @CandidateId, @Disposition, @Score,
                CAST(@ReasonCodes AS jsonb), CAST(@RequiredActions AS jsonb),
                @ReviewAfter, @CreatedAt);
            """,
            new
            {
                Id = Guid.NewGuid(),
                EvidenceId = evidence.Id,
                CandidateId = candidate.Id,
                Disposition = (short)decision.Disposition,
                decision.Score,
                ReasonCodes = JsonSerializer.Serialize(decision.ReasonCodes, JsonOptions),
                RequiredActions = JsonSerializer.Serialize(decision.RequiredActions, JsonOptions),
                decision.ReviewAfter,
                CreatedAt = DateTimeOffset.UtcNow
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (decision.Disposition is AdmissionDisposition.EvidenceOnly)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PersistMemoryResult(evidence.Id, null);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtext(@DedupeKey));",
            new { DedupeKey = dedupeKey },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var existing = await connection.QuerySingleOrDefaultAsync<ClaimRow>(
            new CommandDefinition(
                $"{ClaimSelect} WHERE tenant_id = @TenantId AND dedupe_key = @DedupeKey;",
                new
                {
                    TenantId = evidence.Identity.TenantId,
                    DedupeKey = dedupeKey
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (existing is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE memory_claims
                SET reinforcement_count = reinforcement_count + 1,
                    confidence = GREATEST(confidence, @Confidence),
                    updated_at = @UpdatedAt
                WHERE id = @ClaimId;

                INSERT INTO memory_evidence (claim_id, evidence_id, created_at)
                VALUES (@ClaimId, @EvidenceId, @CreatedAt)
                ON CONFLICT DO NOTHING;
                """,
                new
                {
                    ClaimId = existing.Id,
                    EvidenceId = evidence.Id,
                    Confidence = ConfidenceFrom(decision),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PersistMemoryResult(
                evidence.Id,
                existing.ToDomain() with
                {
                    Confidence = Math.Max(existing.Confidence, ConfidenceFrom(decision)),
                    ReinforcementCount = existing.ReinforcementCount + 1,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
        }

        var status = StatusFrom(decision.Disposition);
        var now = DateTimeOffset.UtcNow;
        var claimId = Guid.NewGuid();

        if (status == MemoryStatus.Active &&
            candidate.ResolutionPolicy == ResolutionPolicy.TemporalLatest &&
            !string.IsNullOrWhiteSpace(candidate.MemoryKey))
        {
            await SupersedeSameKeyAndScopeAsync(
                connection,
                transaction,
                evidence.Identity.TenantId,
                candidate,
                scopeHash,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO memory_claims (
                id, tenant_id, memory_key, kind, resolution_policy, status,
                statement, normalized_statement, scope, scope_hash, confidence,
                current_version, reinforcement_count, dedupe_key,
                created_at, updated_at, expires_at)
            VALUES (
                @Id, @TenantId, @MemoryKey, @Kind, @ResolutionPolicy, @Status,
                @Statement, @NormalizedStatement, CAST(@Scope AS jsonb), @ScopeHash,
                @Confidence, 1, 1, @DedupeKey, @CreatedAt, @UpdatedAt, @ExpiresAt);

            INSERT INTO memory_claim_versions (
                claim_id, version, status, statement, scope, confidence, reason, created_at)
            VALUES (
                @Id, 1, @Status, @Statement, CAST(@Scope AS jsonb),
                @Confidence, @Reason, @CreatedAt);

            INSERT INTO memory_evidence (claim_id, evidence_id, created_at)
            VALUES (@Id, @EvidenceId, @CreatedAt);
            """,
            new
            {
                Id = claimId,
                TenantId = evidence.Identity.TenantId,
                candidate.MemoryKey,
                Kind = (short)candidate.Kind,
                ResolutionPolicy = (short)candidate.ResolutionPolicy,
                Status = (short)status,
                Statement = candidate.Statement,
                NormalizedStatement = candidate.Statement.ToLowerInvariant(),
                Scope = JsonSerializer.Serialize(candidate.Scope, JsonOptions),
                ScopeHash = scopeHash,
                Confidence = ConfidenceFrom(decision),
                DedupeKey = dedupeKey,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = ExpiryFor(decision),
                Reason = string.Join(',', decision.ReasonCodes),
                EvidenceId = evidence.Id
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new PersistMemoryResult(
            evidence.Id,
            new MemoryClaim(
                claimId,
                evidence.Identity.TenantId,
                candidate.MemoryKey,
                candidate.Kind,
                candidate.ResolutionPolicy,
                status,
                candidate.Statement,
                candidate.Scope,
                ConfidenceFrom(decision),
                1,
                1,
                now,
                now,
                ExpiryFor(decision)));
    }

    public async Task<IReadOnlyList<MemoryClaim>> SearchAsync(
        string tenantId,
        string query,
        bool includeProbation,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = await connection.QueryAsync<ClaimRow>(new CommandDefinition(
            """
            SELECT
                id AS Id,
                tenant_id AS TenantId,
                memory_key AS MemoryKey,
                kind AS Kind,
                resolution_policy AS ResolutionPolicy,
                status AS Status,
                statement AS Statement,
                scope::text AS ScopeJson,
                confidence AS Confidence,
                current_version AS CurrentVersion,
                reinforcement_count AS ReinforcementCount,
                created_at AS CreatedAt,
                updated_at AS UpdatedAt,
                expires_at AS ExpiresAt,
                CASE
                    WHEN @Query = '' THEN 0.20
                    ELSE GREATEST(
                        similarity(statement, @Query),
                        CASE WHEN statement ILIKE ('%' || @Query || '%') THEN 0.70 ELSE 0 END)
                END AS TextScore
            FROM memory_claims
            WHERE tenant_id = @TenantId
              AND (
                    status = @ActiveStatus
                    OR (@IncludeProbation AND status = @ProbationStatus)
                  )
              AND (expires_at IS NULL OR expires_at > @Now)
              AND (
                    @Query = ''
                    OR statement ILIKE ('%' || @Query || '%')
                    OR similarity(statement, @Query) >= 0.05
                  )
            ORDER BY TextScore DESC, updated_at DESC
            LIMIT @Limit;
            """,
            new
            {
                TenantId = tenantId,
                Query = query,
                IncludeProbation = includeProbation,
                ActiveStatus = (short)MemoryStatus.Active,
                ProbationStatus = (short)MemoryStatus.Probation,
                Now = DateTimeOffset.UtcNow,
                Limit = Math.Clamp(limit, 1, 500)
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(row => row.ToDomain()).ToArray();
    }

    public async Task<MemoryExplanation?> ExplainAsync(
        string tenantId,
        Guid claimId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var claimRow = await connection.QuerySingleOrDefaultAsync<ClaimRow>(
            new CommandDefinition(
                $"{ClaimSelect} WHERE tenant_id = @TenantId AND id = @ClaimId;",
                new { TenantId = tenantId, ClaimId = claimId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (claimRow is null)
        {
            return null;
        }

        var evidenceRows = await connection.QueryAsync<EvidenceSummaryRow>(
            new CommandDefinition(
                """
                SELECT
                    e.id AS EvidenceId,
                    e.source_type AS SourceType,
                    e.trust AS Trust,
                    e.source_reference AS SourceReference,
                    e.actor_id AS ActorId,
                    LEFT(e.content, 240) AS ContentPreview,
                    e.occurred_at AS OccurredAt
                FROM memory_evidence me
                INNER JOIN evidence_events e ON e.id = me.evidence_id
                WHERE me.claim_id = @ClaimId
                ORDER BY e.occurred_at DESC;
                """,
                new { ClaimId = claimId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var admissionRows = await connection.QueryAsync<AdmissionSummaryRow>(
            new CommandDefinition(
                """
                SELECT
                    a.disposition AS Disposition,
                    a.score AS Score,
                    a.reason_codes::text AS ReasonCodesJson,
                    a.created_at AS CreatedAt
                FROM admission_decisions a
                INNER JOIN memory_evidence me ON me.evidence_id = a.evidence_id
                WHERE me.claim_id = @ClaimId
                ORDER BY a.created_at DESC;
                """,
                new { ClaimId = claimId },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new MemoryExplanation(
            claimRow.ToDomain(),
            evidenceRows.Select(row => row.ToDomain()).ToArray(),
            admissionRows.Select(row => row.ToDomain()).ToArray());
    }

    public async Task<AuthenticatedDevice?> AuthenticateAsync(
        string tokenHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var device = await connection.QuerySingleOrDefaultAsync<DeviceRow>(
            new CommandDefinition(
                """
                UPDATE edge_devices
                SET last_seen_at = @Now
                WHERE token_hash = @TokenHash
                  AND is_active = true
                RETURNING
                    id AS DeviceId,
                    tenant_id AS TenantId,
                    principal_id AS PrincipalId,
                    device_name AS DeviceName,
                    is_active AS IsActive;
                """,
                new
                {
                    TokenHash = tokenHash,
                    Now = DateTimeOffset.UtcNow
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return device?.ToDomain();
    }

    public async Task<DeviceRegistrationResponse> RegisterAsync(
        DeviceRegistrationRequest request,
        string rawToken,
        string tokenHash,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO edge_devices (
                id, tenant_id, principal_id, device_name,
                token_hash, is_active, created_at)
            VALUES (
                @Id, @TenantId, @PrincipalId, @DeviceName,
                @TokenHash, true, @CreatedAt);
            """,
            new
            {
                Id = id,
                TenantId = request.TenantId.Trim(),
                PrincipalId = request.PrincipalId.Trim(),
                DeviceName = request.DeviceName.Trim(),
                TokenHash = tokenHash,
                CreatedAt = now
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new DeviceRegistrationResponse(
            id,
            rawToken,
            request.TenantId.Trim(),
            request.PrincipalId.Trim(),
            now);
    }

    private static async Task SupersedeSameKeyAndScopeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tenantId,
        MemoryCandidate candidate,
        string scopeHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var oldClaims = await connection.QueryAsync<ClaimRow>(
            new CommandDefinition(
                $"""
                {ClaimSelect}
                WHERE tenant_id = @TenantId
                  AND kind = @Kind
                  AND memory_key = @MemoryKey
                  AND scope_hash = @ScopeHash
                  AND status = @ActiveStatus;
                """,
                new
                {
                    TenantId = tenantId,
                    Kind = (short)candidate.Kind,
                    candidate.MemoryKey,
                    ScopeHash = scopeHash,
                    ActiveStatus = (short)MemoryStatus.Active
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var oldClaim in oldClaims)
        {
            var nextVersion = oldClaim.CurrentVersion + 1;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE memory_claims
                SET status = @Status,
                    current_version = @Version,
                    updated_at = @UpdatedAt
                WHERE id = @ClaimId;

                INSERT INTO memory_claim_versions (
                    claim_id, version, status, statement, scope,
                    confidence, reason, created_at)
                VALUES (
                    @ClaimId, @Version, @Status, @Statement,
                    CAST(@Scope AS jsonb), @Confidence,
                    'superseded-by-newer-exact-scope-claim', @UpdatedAt);
                """,
                new
                {
                    ClaimId = oldClaim.Id,
                    Version = nextVersion,
                    Status = (short)MemoryStatus.Superseded,
                    oldClaim.Statement,
                    Scope = oldClaim.ScopeJson,
                    oldClaim.Confidence,
                    UpdatedAt = now
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static MemoryStatus StatusFrom(
        AdmissionDisposition disposition) =>
        disposition switch
        {
            AdmissionDisposition.Active => MemoryStatus.Active,
            AdmissionDisposition.Probation => MemoryStatus.Probation,
            AdmissionDisposition.Quarantine => MemoryStatus.Quarantined,
            AdmissionDisposition.HumanReview => MemoryStatus.PendingReview,
            AdmissionDisposition.PendingValidation => MemoryStatus.PendingReview,
            _ => MemoryStatus.Probation
        };

    private static double ConfidenceFrom(AdmissionDecision decision) =>
        Math.Clamp(decision.Score / 100.0, 0.05, 1.0);

    private static DateTimeOffset? ExpiryFor(AdmissionDecision decision) =>
        decision.Disposition switch
        {
            AdmissionDisposition.Probation =>
                decision.ReviewAfter ?? DateTimeOffset.UtcNow.AddDays(7),
            AdmissionDisposition.Quarantine =>
                DateTimeOffset.UtcNow.AddDays(30),
            _ => null
        };

    private const string ClaimSelect = """
        SELECT
            id AS Id,
            tenant_id AS TenantId,
            memory_key AS MemoryKey,
            kind AS Kind,
            resolution_policy AS ResolutionPolicy,
            status AS Status,
            statement AS Statement,
            scope::text AS ScopeJson,
            confidence AS Confidence,
            current_version AS CurrentVersion,
            reinforcement_count AS ReinforcementCount,
            created_at AS CreatedAt,
            updated_at AS UpdatedAt,
            expires_at AS ExpiresAt,
            0.0::double precision AS TextScore
        FROM memory_claims
        """;

    private sealed class ClaimRow
    {
        public Guid Id { get; init; }
        public string TenantId { get; init; } = string.Empty;
        public string? MemoryKey { get; init; }
        public short Kind { get; init; }
        public short ResolutionPolicy { get; init; }
        public short Status { get; init; }
        public string Statement { get; init; } = string.Empty;
        public string ScopeJson { get; init; } = "{}";
        public double Confidence { get; init; }
        public int CurrentVersion { get; init; }
        public int ReinforcementCount { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public double TextScore { get; init; }

        public MemoryClaim ToDomain() =>
            new(
                Id,
                TenantId,
                MemoryKey,
                (MemoryKind)Kind,
                (ResolutionPolicy)ResolutionPolicy,
                (MemoryStatus)Status,
                Statement,
                JsonSerializer.Deserialize<MemoryScope>(ScopeJson, JsonOptions) ?? new(),
                Confidence,
                CurrentVersion,
                ReinforcementCount,
                CreatedAt,
                UpdatedAt,
                ExpiresAt,
                TextScore);
    }

    private sealed class EvidenceSummaryRow
    {
        public Guid EvidenceId { get; init; }
        public string SourceType { get; init; } = string.Empty;
        public short Trust { get; init; }
        public string? SourceReference { get; init; }
        public string ActorId { get; init; } = string.Empty;
        public string ContentPreview { get; init; } = string.Empty;
        public DateTimeOffset OccurredAt { get; init; }

        public EvidenceSummary ToDomain() =>
            new(
                EvidenceId,
                SourceType,
                (SourceTrust)Trust,
                SourceReference,
                ActorId,
                ContentPreview,
                OccurredAt);
    }

    private sealed class AdmissionSummaryRow
    {
        public short Disposition { get; init; }
        public double Score { get; init; }
        public string ReasonCodesJson { get; init; } = "[]";
        public DateTimeOffset CreatedAt { get; init; }

        public AdmissionSummary ToDomain() =>
            new(
                (AdmissionDisposition)Disposition,
                Score,
                JsonSerializer.Deserialize<string[]>(ReasonCodesJson, JsonOptions) ?? [],
                CreatedAt);
    }

    private sealed class DeviceRow
    {
        public Guid DeviceId { get; init; }
        public string TenantId { get; init; } = string.Empty;
        public string PrincipalId { get; init; } = string.Empty;
        public string DeviceName { get; init; } = string.Empty;
        public bool IsActive { get; init; }

        public AuthenticatedDevice ToDomain() =>
            new(DeviceId, TenantId, PrincipalId, DeviceName, IsActive);
    }
}
