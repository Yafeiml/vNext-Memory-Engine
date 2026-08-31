namespace VNext.Memory.Domain;

public sealed record RequestIdentity(
    string TenantId,
    string PrincipalId,
    Guid? DeviceId,
    string ActorId,
    string? SessionId,
    bool IsAdministrator = false);

public sealed record MemoryRecordRequest
{
    public required string Content { get; init; }
    public MemoryKind Kind { get; init; } = MemoryKind.Note;
    public SourceTrust Trust { get; init; } = SourceTrust.AgentInferred;
    public MemoryScope Scope { get; init; } = new();
    public string? MemoryKey { get; init; }
    public string? SourceType { get; init; }
    public string? SourceReference { get; init; }
    public string? AgentId { get; init; }
    public string? SessionId { get; init; }
    public bool ExplicitRemember { get; init; }
    public bool IsCorrection { get; init; }
    public bool HasDeterministicEvidence { get; init; }
    public DateTimeOffset? OccurredAt { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record MemorySearchRequest
{
    public required string Query { get; init; }
    public MemoryScope Scope { get; init; } = new();
    public IReadOnlyList<MemoryKind>? Kinds { get; init; }
    public bool IncludeProbation { get; init; } = true;
    public int Limit { get; init; } = 10;
}

public sealed record ContextCompileRequest
{
    public required string Task { get; init; }
    public MemoryScope Scope { get; init; } = new();
    public IReadOnlyList<string>? Files { get; init; }
    public int TokenBudget { get; init; } = 1800;
    public bool IncludeProbation { get; init; } = true;
}

public sealed record AdmissionDecision(
    AdmissionDisposition Disposition,
    double Score,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> RequiredActions,
    DateTimeOffset? ReviewAfter = null);

public sealed record MemoryCandidate(
    Guid Id,
    string Statement,
    string? MemoryKey,
    MemoryKind Kind,
    SourceTrust Trust,
    MemoryScope Scope,
    ResolutionPolicy ResolutionPolicy,
    string? SourceType,
    string? SourceReference,
    bool ExplicitRemember,
    bool IsCorrection,
    bool HasDeterministicEvidence,
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> Tags);

public sealed record EvidenceEvent(
    Guid Id,
    RequestIdentity Identity,
    MemoryScope Scope,
    string Content,
    MemoryKind Kind,
    SourceTrust Trust,
    string SourceType,
    string? SourceReference,
    string ContentHash,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt);

public sealed record MemoryClaim(
    Guid Id,
    string TenantId,
    string? MemoryKey,
    MemoryKind Kind,
    ResolutionPolicy ResolutionPolicy,
    MemoryStatus Status,
    string Statement,
    MemoryScope Scope,
    double Confidence,
    int CurrentVersion,
    int ReinforcementCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt,
    double TextScore = 0);

public sealed record RecordMemoryResult(
    Guid? EvidenceId,
    Guid? ClaimId,
    AdmissionDisposition Disposition,
    double Score,
    MemoryScope AppliedScope,
    IReadOnlyList<string> ReasonCodes,
    bool Queued = false,
    string? Message = null);

public sealed record MemorySearchHit(
    Guid ClaimId,
    string? MemoryKey,
    MemoryKind Kind,
    MemoryStatus Status,
    string Statement,
    MemoryScope Scope,
    double Confidence,
    double Relevance,
    DateTimeOffset UpdatedAt,
    string ProvenanceLabel);

public sealed record ContextSection(
    string Name,
    IReadOnlyList<MemorySearchHit> Items);

public sealed record MemoryContextPacket(
    string Task,
    MemoryScope Scope,
    IReadOnlyList<ContextSection> Sections,
    int EstimatedTokens,
    DateTimeOffset CompiledAt);

public sealed record EvidenceSummary(
    Guid EvidenceId,
    string SourceType,
    SourceTrust Trust,
    string? SourceReference,
    string ActorId,
    string ContentPreview,
    DateTimeOffset OccurredAt);

public sealed record AdmissionSummary(
    AdmissionDisposition Disposition,
    double Score,
    IReadOnlyList<string> ReasonCodes,
    DateTimeOffset CreatedAt);

public sealed record MemoryExplanation(
    MemoryClaim Claim,
    IReadOnlyList<EvidenceSummary> Evidence,
    IReadOnlyList<AdmissionSummary> AdmissionHistory);

public sealed record DeviceRegistrationRequest(
    string TenantId,
    string PrincipalId,
    string DeviceName);

public sealed record DeviceRegistrationResponse(
    Guid DeviceId,
    string DeviceToken,
    string TenantId,
    string PrincipalId,
    DateTimeOffset CreatedAt);

public sealed record AuthenticatedDevice(
    Guid DeviceId,
    string TenantId,
    string PrincipalId,
    string DeviceName,
    bool IsActive);

public sealed record PersistMemoryResult(
    Guid EvidenceId,
    MemoryClaim? Claim);
