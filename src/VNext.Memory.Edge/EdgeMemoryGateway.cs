using Microsoft.Extensions.Options;
using VNext.Memory.Domain;

namespace VNext.Memory.Edge;

public interface IEdgeMemoryGateway
{
    Task<RecordMemoryResult> RecordAsync(
        MemoryRecordRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        MemorySearchRequest request,
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

    public async Task<RecordMemoryResult> RecordAsync(
        MemoryRecordRequest request,
        CancellationToken cancellationToken = default)
    {
        var actorId = ResolveActor(request.AgentId);
        var sessionId = request.SessionId;

        try
        {
            return await coreClient
                .RecordAsync(request, actorId, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CoreMemoryRequestException exception) when (exception.Retryable)
        {
            logger.LogWarning(
                exception,
                "Memory Core is temporarily unavailable. Queuing observation locally.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                exception,
                "Memory Core is unreachable. Queuing observation locally.");
        }
        catch (TaskCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Memory Core timed out. Queuing observation locally.");
        }

        await outbox
            .EnqueueAsync(request, actorId, sessionId, cancellationToken)
            .ConfigureAwait(false);

        return new RecordMemoryResult(
            null,
            null,
            AdmissionDisposition.EvidenceOnly,
            0,
            request.Scope,
            ["QUEUED_AT_EDGE"],
            Queued: true,
            Message: "Observation is stored in the local outbox and will be synchronized.");
    }

    public Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        MemorySearchRequest request,
        CancellationToken cancellationToken = default) =>
        coreClient.SearchAsync(
            request,
            _options.DefaultActorId,
            null,
            cancellationToken);

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
