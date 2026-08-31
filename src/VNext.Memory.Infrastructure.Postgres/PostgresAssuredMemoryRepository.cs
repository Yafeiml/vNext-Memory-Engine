using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using VNext.Memory.Domain;

namespace VNext.Memory.Infrastructure.Postgres;

public sealed class PostgresAssuredMemoryRepository(
    PostgresMemoryRepository inner,
    NpgsqlDataSource dataSource) : IMemoryRepository
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
        await using var guardConnection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var lockKey = $"vme-evidence:{evidence.Id:D}";

        await guardConnection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_lock(hashtext(@LockKey));",
            new { LockKey = lockKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        try
        {
            var replay = await guardConnection
                .QuerySingleOrDefaultAsync<ReplayRow>(new CommandDefinition(
                    """
                    SELECT
                        e.tenant_id AS TenantId,
                        e.id AS EvidenceId,
                        me.claim_id AS ClaimId
                    FROM evidence_events e
                    LEFT JOIN memory_evidence me ON me.evidence_id = e.id
                    WHERE e.id = @EvidenceId
                    LIMIT 1;
                    """,
                    new { EvidenceId = evidence.Id },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            if (replay is not null)
            {
                if (!string.Equals(
                        replay.TenantId,
                        evidence.Identity.TenantId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Evidence event identifier is already owned by another tenant.");
                }

                await UpsertAssuranceAsync(
                    guardConnection,
                    evidence,
                    cancellationToken).ConfigureAwait(false);

                MemoryClaim? claim = null;
                if (replay.ClaimId is Guid claimId)
                {
                    claim = (await inner.ExplainAsync(
                        evidence.Identity.TenantId,
                        claimId,
                        cancellationToken).ConfigureAwait(false))?.Claim;
                }

                return new PersistMemoryResult(
                    replay.EvidenceId,
                    claim,
                    IsReplay: true);
            }

            var persisted = await inner.PersistAsync(
                evidence,
                candidate,
                decision,
                dedupeKey,
                scopeHash,
                cancellationToken).ConfigureAwait(false);

            await UpsertAssuranceAsync(
                guardConnection,
                evidence,
                cancellationToken).ConfigureAwait(false);

            return persisted;
        }
        finally
        {
            await guardConnection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(hashtext(@LockKey));",
                new { LockKey = lockKey },
                cancellationToken: CancellationToken.None)).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyList<MemoryClaim>> SearchAsync(
        string tenantId,
        string query,
        bool includeProbation,
        int limit,
        CancellationToken cancellationToken) =>
        inner.SearchAsync(
            tenantId,
            query,
            includeProbation,
            limit,
            cancellationToken);

    public async Task<MemoryExplanation?> ExplainAsync(
        string tenantId,
        Guid claimId,
        CancellationToken cancellationToken)
    {
        var explanation = await inner.ExplainAsync(
            tenantId,
            claimId,
            cancellationToken).ConfigureAwait(false);

        if (explanation is null || explanation.Evidence.Count == 0)
        {
            return explanation;
        }

        var evidenceIds = explanation.Evidence
            .Select(item => item.EvidenceId)
            .Distinct()
            .ToArray();

        await using var connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var assuranceRows = await connection.QueryAsync<AssuranceRow>(
            new CommandDefinition(
                """
                SELECT
                    evidence_id AS EvidenceId,
                    event_id AS EventId,
                    channel AS Channel,
                    assurance_level AS AssuranceLevel,
                    claimed_trust AS ClaimedTrust,
                    effective_trust AS EffectiveTrust,
                    signature_verified AS SignatureVerified,
                    explicit_remember_verified AS ExplicitRememberVerified,
                    correction_verified AS CorrectionVerified,
                    deterministic_verified AS DeterministicVerified,
                    adapter AS Adapter,
                    adapter_version AS AdapterVersion,
                    payload_hash AS PayloadHash,
                    proof::text AS ProofJson,
                    reason_codes::text AS ReasonCodesJson
                FROM evidence_assurance
                WHERE evidence_id = ANY(@EvidenceIds);
                """,
                new { EvidenceIds = evidenceIds },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var assuranceByEvidence = assuranceRows.ToDictionary(
            row => row.EvidenceId,
            row => row.ToDomain());

        return explanation with
        {
            Evidence = explanation.Evidence
                .Select(item => assuranceByEvidence.TryGetValue(
                        item.EvidenceId,
                        out var assurance)
                    ? item with { Assurance = assurance }
                    : item)
                .ToArray()
        };
    }

    private static async Task UpsertAssuranceAsync(
        NpgsqlConnection connection,
        EvidenceEvent evidence,
        CancellationToken cancellationToken)
    {
        var assurance = evidence.Assurance ?? new SourceAssurance(
            EvidenceChannel.LegacyClient,
            AssuranceLevel.Authenticated,
            evidence.Trust,
            evidence.Trust,
            SignatureVerified: false,
            ExplicitRememberVerified: false,
            CorrectionVerified: false,
            DeterministicEvidenceVerified: false,
            "legacy-repository",
            null,
            null,
            evidence.Id,
            null,
            ["LEGACY_ASSURANCE_SYNTHESIZED"]);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO evidence_assurance (
                evidence_id, event_id, channel, assurance_level,
                claimed_trust, effective_trust, signature_verified,
                explicit_remember_verified, correction_verified,
                deterministic_verified, adapter, adapter_version,
                payload_hash, proof, reason_codes, created_at)
            VALUES (
                @EvidenceId, @EventId, @Channel, @AssuranceLevel,
                @ClaimedTrust, @EffectiveTrust, @SignatureVerified,
                @ExplicitRememberVerified, @CorrectionVerified,
                @DeterministicVerified, @Adapter, @AdapterVersion,
                @PayloadHash, CAST(@Proof AS jsonb),
                CAST(@ReasonCodes AS jsonb), @CreatedAt)
            ON CONFLICT (evidence_id) DO UPDATE SET
                event_id = EXCLUDED.event_id,
                channel = EXCLUDED.channel,
                assurance_level = EXCLUDED.assurance_level,
                claimed_trust = EXCLUDED.claimed_trust,
                effective_trust = EXCLUDED.effective_trust,
                signature_verified = EXCLUDED.signature_verified,
                explicit_remember_verified = EXCLUDED.explicit_remember_verified,
                correction_verified = EXCLUDED.correction_verified,
                deterministic_verified = EXCLUDED.deterministic_verified,
                adapter = EXCLUDED.adapter,
                adapter_version = EXCLUDED.adapter_version,
                payload_hash = EXCLUDED.payload_hash,
                proof = EXCLUDED.proof,
                reason_codes = EXCLUDED.reason_codes;
            """,
            new
            {
                EvidenceId = evidence.Id,
                EventId = assurance.EventId ?? evidence.Id,
                Channel = (short)assurance.Channel,
                AssuranceLevel = (short)assurance.Level,
                ClaimedTrust = (short)assurance.ClaimedTrust,
                EffectiveTrust = (short)assurance.EffectiveTrust,
                assurance.SignatureVerified,
                assurance.ExplicitRememberVerified,
                assurance.CorrectionVerified,
                DeterministicVerified = assurance.DeterministicEvidenceVerified,
                assurance.Adapter,
                assurance.AdapterVersion,
                assurance.PayloadHash,
                Proof = assurance.Proof is null
                    ? null
                    : JsonSerializer.Serialize(assurance.Proof, JsonOptions),
                ReasonCodes = JsonSerializer.Serialize(
                    assurance.ReasonCodes,
                    JsonOptions),
                CreatedAt = DateTimeOffset.UtcNow
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed class ReplayRow
    {
        public string TenantId { get; init; } = string.Empty;
        public Guid EvidenceId { get; init; }
        public Guid? ClaimId { get; init; }
    }

    private sealed class AssuranceRow
    {
        public Guid EvidenceId { get; init; }
        public Guid EventId { get; init; }
        public short Channel { get; init; }
        public short AssuranceLevel { get; init; }
        public short ClaimedTrust { get; init; }
        public short EffectiveTrust { get; init; }
        public bool SignatureVerified { get; init; }
        public bool ExplicitRememberVerified { get; init; }
        public bool CorrectionVerified { get; init; }
        public bool DeterministicVerified { get; init; }
        public string Adapter { get; init; } = string.Empty;
        public string? AdapterVersion { get; init; }
        public string? PayloadHash { get; init; }
        public string? ProofJson { get; init; }
        public string ReasonCodesJson { get; init; } = "[]";

        public SourceAssurance ToDomain() =>
            new(
                (EvidenceChannel)Channel,
                (AssuranceLevel)AssuranceLevel,
                (SourceTrust)ClaimedTrust,
                (SourceTrust)EffectiveTrust,
                SignatureVerified,
                ExplicitRememberVerified,
                CorrectionVerified,
                DeterministicVerified,
                Adapter,
                AdapterVersion,
                string.IsNullOrWhiteSpace(ProofJson)
                    ? null
                    : JsonSerializer.Deserialize<EvidenceProof>(ProofJson, JsonOptions),
                EventId,
                PayloadHash,
                JsonSerializer.Deserialize<string[]>(
                    ReasonCodesJson,
                    JsonOptions) ?? []);
    }
}
