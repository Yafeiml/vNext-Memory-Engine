using Microsoft.Extensions.Options;
using VNext.Memory.Domain;

namespace VNext.Memory.Edge;

public sealed record CapturedObservation(
    MemoryRecordRequest Observation,
    EvidenceChannel Channel,
    string Adapter,
    string? AdapterVersion = null,
    EvidenceProof? Proof = null,
    Guid? EventId = null,
    DateTimeOffset? CapturedAt = null);

public interface IEdgeMemoryGateway
{
    Task<RecordMemoryResult> RecordAsync(
        MemoryRecordRequest request,
        CancellationToken cancellationToken = default);

    Task<RecordMemoryResult> RecordCapturedAsync(
        CapturedObservation captured,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        MemorySearchRequest request,
        CancellationToken cancellationToken = default);

    Task<MemorySearchResponse> SearchWithTraceAsync(
        MemorySearchRequest request,
        CancellationToken cancellationToken = default);

    Task<RetrievalFeedbackResult> RecordFeedbackAsync(
        RetrievalFeedbackRequest request,
        CancellationToken cancellationToken = default);

    Task<MemoryContextPacket> CompileContextAsync(
        ContextCompileRequest request,
        CancellationToken cancellationToken = default);

    Task<MemoryExplanation?> ExplainAsync(
        Guid claimId,
        string? actorId,
        string? sessionId,
        CancellationToken cancellationToken = default);
}

public sealed class EdgeMemoryGateway(
    CoreMemoryClient coreClient,
    SqliteOutbox outbox,
    IOptions<EdgeOptions> options,
    ILogger<EdgeMemoryGateway> logger) : IEdgeMemoryGateway
{
    private readonly EdgeOptions _options = options.Value;

    public Task<RecordMemoryResult> RecordAsync(
        MemoryRecordRequest request,
        CancellationToken cancellationToken = default) =>
        RecordCapturedAsync(
            new CapturedObservation(
                request,
                EvidenceChannel.AgentObservation,
                "vnext-memory-edge",
                "0.2.0"),
            cancellationToken);

    public async Task<RecordMemoryResult> RecordCapturedAsync(
        CapturedObservation captured,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(captured.Observation);

        var actorId = ResolveActor(captured.Observation.AgentId);
        var sessionId = captured.Observation.SessionId;
        var envelope = EvidenceEnvelopeCryptography.SignEvidence(
            captured.Observation,
            captured.Channel,
            captured.Adapter,
            _options.CoreToken,
            captured.Proof,
            captured.AdapterVersion,
            captured.EventId,
            captured.CapturedAt);

        try
        {
            return await coreClient
                .IngestAsync(envelope, actorId, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CoreMemoryRequestException exception) when (exception.Retryable)
        {
            logger.LogWarning(
                exception,
                "Memory Core is temporarily unavailable. Queuing signed evidence locally.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                exception,
                "Memory Core is unreachable. Queuing signed evidence locally.");
        }
        catch (TaskCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Memory Core timed out. Queuing signed evidence locally.");
        }

        await outbox
            .EnqueueAsync(envelope, cancellationToken)
            .ConfigureAwait(false);

        return new RecordMemoryResult(
            null,
            null,
            AdmissionDisposition.EvidenceOnly,
            0,
            captured.Observation.Scope,
            ["QUEUED_AT_EDGE", "SIGNED_ENVELOPE_PRESERVED"],
            Queued: true,
            Message: $"Signed evidence {envelope.EventId:D} is stored in the local outbox.");
    }

    public Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        MemorySearchRequest request,
        CancellationToken cancellationToken = default) =>
        coreClient.SearchAsync(
            request,
            _options.DefaultActorId,
            null,
            cancellationToken);

    public Task<MemorySearchResponse> SearchWithTraceAsync(
        MemorySearchRequest request,
        CancellationToken cancellationToken = default) =>
        coreClient.SearchWithTraceAsync(
            request,
            _options.DefaultActorId,
            null,
            cancellationToken);

    public Task<RetrievalFeedbackResult> RecordFeedbackAsync(
        RetrievalFeedbackRequest request,
        CancellationToken cancellationToken = default)
    {
        var envelope = EvidenceEnvelopeCryptography.SignFeedback(
            request,
            EvidenceChannel.OutcomeFeedback,
            "vnext-memory-edge",
            _options.CoreToken,
            adapterVersion: "0.2.0");

        return coreClient.FeedbackAsync(
            envelope,
            _options.DefaultActorId,
            null,
            cancellationToken);
    }

    public Task<MemoryContextPacket> CompileContextAsync(
        ContextCompileRequest request,
        CancellationToken cancellationToken = default) =>
        coreClient.CompileContextAsync(
            request,
            _options.DefaultActorId,
            null,
            cancellationToken);

    public Task<MemoryExplanation?> ExplainAsync(
        Guid claimId,
        string? actorId,
        string? sessionId,
        CancellationToken cancellationToken = default) =>
        coreClient.ExplainAsync(
            claimId,
            ResolveActor(actorId),
            sessionId,
            cancellationToken);

    private string ResolveActor(string? actorId) =>
        string.IsNullOrWhiteSpace(actorId)
            ? _options.DefaultActorId
            : actorId.Trim();
}
