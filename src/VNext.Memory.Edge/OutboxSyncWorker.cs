using Microsoft.Extensions.Options;

namespace VNext.Memory.Edge;

public sealed class OutboxSyncWorker(
    SqliteOutbox outbox,
    CoreMemoryClient coreClient,
    IOptions<EdgeOptions> options,
    ILogger<OutboxSyncWorker> logger) : BackgroundService
{
    private readonly EdgeOptions _options = options.Value;

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await outbox.InitializeAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(
                Math.Clamp(_options.SyncIntervalSeconds, 1, 300)));

        await FlushAsync(stoppingToken).ConfigureAwait(false);

        while (await timer
            .WaitForNextTickAsync(stoppingToken)
            .ConfigureAwait(false))
        {
            await FlushAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var items = await outbox
            .GetDueAsync(_options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in items)
        {
            try
            {
                var actorId = string.IsNullOrWhiteSpace(item.Envelope.Observation.AgentId)
                    ? _options.DefaultActorId
                    : item.Envelope.Observation.AgentId;

                await coreClient
                    .IngestAsync(
                        item.Envelope,
                        actorId,
                        item.Envelope.Observation.SessionId,
                        cancellationToken)
                    .ConfigureAwait(false);

                await outbox
                    .MarkSucceededAsync(item.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is HttpRequestException or
                      TaskCanceledException or
                      CoreMemoryRequestException)
            {
                logger.LogWarning(
                    exception,
                    "Failed to synchronize signed outbox item {OutboxId}.",
                    item.Id);

                await outbox
                    .MarkFailedAsync(
                        item.Id,
                        item.Attempts + 1,
                        exception.Message,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (exception is CoreMemoryRequestException { Retryable: false })
                {
                    continue;
                }

                break;
            }
        }
    }
}
