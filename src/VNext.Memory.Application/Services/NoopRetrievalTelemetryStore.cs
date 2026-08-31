using VNext.Memory.Domain;

namespace VNext.Memory.Application.Services;

public sealed class NoopRetrievalTelemetryStore : IRetrievalTelemetryStore
{
    public static NoopRetrievalTelemetryStore Instance { get; } = new();

    private NoopRetrievalTelemetryStore()
    {
    }

    public Task RecordTraceAsync(
        RetrievalTrace trace,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<RetrievalFeedbackResult> ApplyFeedbackAsync(
        RetrievalFeedbackRequest request,
        RequestIdentity identity,
        SourceAssurance assurance,
        CancellationToken cancellationToken) =>
        Task.FromResult(new RetrievalFeedbackResult(
            request.TraceId,
            request.Outcome,
            0,
            Accepted: false,
            Authoritative: false,
            ["RETRIEVAL_TELEMETRY_STORE_UNAVAILABLE"]));
}
