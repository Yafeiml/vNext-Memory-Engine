namespace VNext.Memory.Domain;

public interface IMemoryAdmissionController
{
    ValueTask<AdmissionDecision> EvaluateAsync(
        MemoryCandidate candidate,
        CancellationToken cancellationToken = default);
}

public interface ISourceAssuranceEvaluator
{
    SourceAssurance Evaluate(
        MemoryRecordRequest request,
        RequestIdentity identity,
        SourceAssertionContext context);

    SourceAssurance EvaluateFeedback(
        RetrievalFeedbackRequest request,
        RequestIdentity identity,
        SourceAssertionContext context);
}

public interface IScopeResolver
{
    MemoryScope InferAndNormalizeScope(
        MemoryRecordRequest request,
        RequestIdentity identity);

    bool IsApplicable(
        MemoryScope memoryScope,
        MemoryScope queryScope);

    double CalculateMatchScore(
        MemoryScope memoryScope,
        MemoryScope queryScope);

    IReadOnlyList<MemoryClaim> Resolve(
        IEnumerable<MemoryClaim> claims,
        MemoryScope queryScope,
        int limit);
}

public interface IMemoryRepository
{
    Task<PersistMemoryResult> PersistAsync(
        EvidenceEvent evidence,
        MemoryCandidate candidate,
        AdmissionDecision decision,
        string dedupeKey,
        string scopeHash,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MemoryClaim>> SearchAsync(
        string tenantId,
        string query,
        bool includeProbation,
        int limit,
        CancellationToken cancellationToken);

    Task<MemoryExplanation?> ExplainAsync(
        string tenantId,
        Guid claimId,
        CancellationToken cancellationToken);
}

public interface IRetrievalTelemetryStore
{
    Task RecordTraceAsync(
        RetrievalTrace trace,
        CancellationToken cancellationToken);

    Task<RetrievalFeedbackResult> ApplyFeedbackAsync(
        RetrievalFeedbackRequest request,
        RequestIdentity identity,
        SourceAssurance assurance,
        CancellationToken cancellationToken);
}

public interface IMemoryService
{
    Task<RecordMemoryResult> RecordAsync(
        MemoryRecordRequest request,
        RequestIdentity identity,
        CancellationToken cancellationToken = default);

    Task<RecordMemoryResult> RecordAsync(
        MemoryRecordRequest request,
        RequestIdentity identity,
        SourceAssertionContext assertionContext,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        MemorySearchRequest request,
        RequestIdentity identity,
        CancellationToken cancellationToken = default);

    Task<MemorySearchResponse> SearchWithTraceAsync(
        MemorySearchRequest request,
        RequestIdentity identity,
        CancellationToken cancellationToken = default);

    Task<MemoryContextPacket> CompileContextAsync(
        ContextCompileRequest request,
        RequestIdentity identity,
        CancellationToken cancellationToken = default);

    Task<RetrievalFeedbackResult> RecordFeedbackAsync(
        RetrievalFeedbackRequest request,
        RequestIdentity identity,
        SourceAssertionContext assertionContext,
        CancellationToken cancellationToken = default);

    Task<MemoryExplanation?> ExplainAsync(
        Guid claimId,
        RequestIdentity identity,
        CancellationToken cancellationToken = default);
}

public interface IDeviceIdentityStore
{
    Task<AuthenticatedDevice?> AuthenticateAsync(
        string tokenHash,
        CancellationToken cancellationToken);

    Task<DeviceRegistrationResponse> RegisterAsync(
        DeviceRegistrationRequest request,
        string rawToken,
        string tokenHash,
        CancellationToken cancellationToken);
}
