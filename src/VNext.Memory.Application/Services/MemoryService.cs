using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VNext.Memory.Domain;

namespace VNext.Memory.Application.Services;

public sealed partial class MemoryService(
    IMemoryRepository repository,
    IMemoryAdmissionController admissionController,
    IScopeResolver scopeResolver) : IMemoryService
{
    private const int MaximumSearchLimit = 100;

    public async Task<RecordMemoryResult> RecordAsync(
        MemoryRecordRequest request,
        RequestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);

        var statement = NormalizeStatement(request.Content);
        var scope = scopeResolver.InferAndNormalizeScope(request, identity);
        var resolutionPolicy = ResolvePolicy(request.Kind, request.MemoryKey);

        var candidate = new MemoryCandidate(
            Guid.NewGuid(),
            statement,
            NormalizeKey(request.MemoryKey),
            request.Kind,
            request.Trust,
            scope,
            resolutionPolicy,
            string.IsNullOrWhiteSpace(request.SourceType)
                ? "agent-observation"
                : request.SourceType.Trim(),
            NormalizeOptional(request.SourceReference),
            request.ExplicitRemember,
            request.IsCorrection,
            request.HasDeterministicEvidence,
            request.OccurredAt ?? DateTimeOffset.UtcNow,
            NormalizeTags(request.Tags));

        var decision = await admissionController
            .EvaluateAsync(candidate, cancellationToken)
            .ConfigureAwait(false);

        if (decision.Disposition == AdmissionDisposition.Reject)
        {
            return new RecordMemoryResult(
                null,
                null,
                decision.Disposition,
                decision.Score,
                scope,
                decision.ReasonCodes,
                Message: "Content was rejected before persistence.");
        }

        var effectiveIdentity = identity with
        {
            ActorId = NormalizeOptional(request.AgentId) ?? identity.ActorId,
            SessionId = NormalizeOptional(request.SessionId) ?? identity.SessionId
        };

        var evidence = new EvidenceEvent(
            Guid.NewGuid(),
            effectiveIdentity,
            scope,
            statement,
            request.Kind,
            request.Trust,
            candidate.SourceType ?? "agent-observation",
            candidate.SourceReference,
            Sha256(statement),
            candidate.OccurredAt,
            DateTimeOffset.UtcNow);

        var scopeHash = Sha256(scope.ToCanonicalString());
        var dedupeKey = Sha256(string.Join(
            "\n",
            request.Kind.ToString(),
            candidate.MemoryKey ?? string.Empty,
            statement.ToLowerInvariant(),
            scopeHash));

        var persisted = await repository
            .PersistAsync(
                evidence,
                candidate,
                decision,
                dedupeKey,
                scopeHash,
                cancellationToken)
            .ConfigureAwait(false);

        return new RecordMemoryResult(
            persisted.EvidenceId,
            persisted.Claim?.Id,
            decision.Disposition,
            decision.Score,
            scope,
            decision.ReasonCodes);
    }

    public async Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        MemorySearchRequest request,
        RequestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);

        var normalizedScope = request.Scope.Normalize(identity.TenantId);
        var limit = Math.Clamp(request.Limit, 1, MaximumSearchLimit);
        var candidates = await repository
            .SearchAsync(
                identity.TenantId,
                request.Query.Trim(),
                request.IncludeProbation,
                Math.Min(MaximumSearchLimit * 3, limit * 10),
                cancellationToken)
            .ConfigureAwait(false);

        var filteredKinds = request.Kinds is { Count: > 0 }
            ? candidates.Where(claim => request.Kinds.Contains(claim.Kind))
            : candidates;

        var resolved = scopeResolver.Resolve(filteredKinds, normalizedScope, limit);

        return resolved
            .Select(claim => ToHit(
                claim,
                scopeResolver.CalculateMatchScore(claim.Scope, normalizedScope)))
            .ToArray();
    }

    public async Task<MemoryContextPacket> CompileContextAsync(
        ContextCompileRequest request,
        RequestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Task);

        var maxItems = Math.Clamp(request.TokenBudget / 100, 4, 30);
        var hits = await SearchAsync(
            new MemorySearchRequest
            {
                Query = request.Task,
                Scope = request.Scope,
                IncludeProbation = request.IncludeProbation,
                Limit = maxItems
            },
            identity,
            cancellationToken).ConfigureAwait(false);

        var sections = hits
            .GroupBy(hit => SectionName(hit.Kind, hit.Status))
            .OrderBy(group => SectionOrder(group.Key))
            .Select(group => new ContextSection(
                group.Key,
                group.Take(8).ToArray()))
            .ToArray();

        var estimatedTokens = hits.Sum(hit =>
            Math.Max(8, (int)Math.Ceiling(hit.Statement.Length / 3.2)));

        return new MemoryContextPacket(
            request.Task,
            request.Scope.Normalize(identity.TenantId),
            sections,
            estimatedTokens,
            DateTimeOffset.UtcNow);
    }

    public Task<MemoryExplanation?> ExplainAsync(
        Guid claimId,
        RequestIdentity identity,
        CancellationToken cancellationToken = default) =>
        repository.ExplainAsync(identity.TenantId, claimId, cancellationToken);

    private static MemorySearchHit ToHit(
        MemoryClaim claim,
        double scopeScore)
    {
        var statusWeight = claim.Status == MemoryStatus.Active ? 1.0 : 0.72;
        var scopeWeight = Math.Max(0, scopeScore) / 150.0;
        var relevance = Math.Clamp(
            claim.TextScore * 0.45 +
            claim.Confidence * 0.35 +
            scopeWeight * 0.20,
            0,
            1) * statusWeight;

        return new MemorySearchHit(
            claim.Id,
            claim.MemoryKey,
            claim.Kind,
            claim.Status,
            claim.Statement,
            claim.Scope,
            claim.Confidence,
            relevance,
            claim.UpdatedAt,
            claim.Status == MemoryStatus.Active
                ? "governed-active-memory"
                : "probation-memory");
    }

    private static ResolutionPolicy ResolvePolicy(
        MemoryKind kind,
        string? memoryKey) =>
        kind switch
        {
            MemoryKind.Preference => ResolutionPolicy.MostSpecificOverride,
            MemoryKind.Constraint => ResolutionPolicy.MostSpecificOverride,
            MemoryKind.TechnicalFact when !string.IsNullOrWhiteSpace(memoryKey) =>
                ResolutionPolicy.TemporalLatest,
            MemoryKind.Decision when !string.IsNullOrWhiteSpace(memoryKey) =>
                ResolutionPolicy.TemporalLatest,
            MemoryKind.FailedApproach => ResolutionPolicy.SetUnion,
            MemoryKind.Hypothesis => ResolutionPolicy.KeepConflicts,
            MemoryKind.Procedure => ResolutionPolicy.AppendOnly,
            MemoryKind.TaskState => ResolutionPolicy.TransactionalLatest,
            _ => ResolutionPolicy.AppendOnly
        };

    private static string SectionName(MemoryKind kind, MemoryStatus status)
    {
        if (status == MemoryStatus.Probation)
        {
            return "Unverified hypotheses";
        }

        return kind switch
        {
            MemoryKind.Preference => "User preferences",
            MemoryKind.Decision => "Relevant decisions",
            MemoryKind.Constraint => "Constraints",
            MemoryKind.FailedApproach => "Past failed approaches",
            MemoryKind.TechnicalFact => "Validated facts",
            MemoryKind.Procedure => "Procedures",
            MemoryKind.Hypothesis => "Open hypotheses",
            _ => "Other relevant memory"
        };
    }

    private static int SectionOrder(string name) =>
        name switch
        {
            "Constraints" => 0,
            "User preferences" => 1,
            "Relevant decisions" => 2,
            "Validated facts" => 3,
            "Past failed approaches" => 4,
            "Procedures" => 5,
            "Open hypotheses" => 6,
            "Unverified hypotheses" => 7,
            _ => 8
        };

    private static string NormalizeStatement(string value) =>
        WhitespaceRegex().Replace(value.Trim(), " ");

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant();

    private static IReadOnlyList<string> NormalizeTags(
        IReadOnlyList<string>? tags) =>
        tags?
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray() ?? [];

    private static string Sha256(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
