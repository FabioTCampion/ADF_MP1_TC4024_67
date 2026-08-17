using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Web;

internal sealed class WeightExportWorker(
    WeightExportOptions options,
    IWeightExportClient client,
    IHistorianRepository repository,
    TimeProvider clock,
    ILogger<WeightExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Weight export to PaperSystem is disabled.");
            return;
        }

        logger.LogInformation(
            "Weight export worker started for machine {MachineId}; only the loopback connector is contacted.",
            options.MachineId);
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow();
            WeightExportOutboxItem? item = null;
            try
            {
                item = await repository.GetNextWeightExportAsync(now, stoppingToken);
                if (item is null)
                {
                    await DelayAsync(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                var receiptId = await client.SendAsync(item, stoppingToken);
                await repository.MarkWeightExportDeliveredAsync(
                    item.Id,
                    receiptId,
                    clock.GetUtcNow(),
                    stoppingToken);
                logger.LogInformation(
                    "Weight event {EventId} revision {Revision} delivered to PaperSystem.",
                    item.EventId,
                    item.Revision);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (WeightExportDeliveryException exception) when (item is not null)
            {
                await RecordFailureAsync(item, exception.Message, exception.Retryable, stoppingToken);
            }
            catch (Exception exception) when (item is not null)
            {
                await RecordFailureAsync(item, exception.Message, retryable: true, stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Weight export queue could not be read.");
                await DelayAsync(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
            }
        }
    }

    private async Task RecordFailureAsync(
        WeightExportOutboxItem item,
        string error,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var safeError = Sanitize(error);
        DateTimeOffset? retryAt = retryable
            ? now + CalculateRetryDelay(item.Attempts)
            : null;
        await repository.RecordWeightExportFailureAsync(
            item.Id,
            safeError,
            now,
            retryAt,
            cancellationToken);
        if (retryable)
        {
            logger.LogWarning(
                "Weight event {EventId} revision {Revision} failed and will retry at {RetryAtUtc}: {Failure}",
                item.EventId,
                item.Revision,
                retryAt,
                safeError);
        }
        else
        {
            logger.LogError(
                "Weight event {EventId} revision {Revision} was suspended after a permanent rejection: {Failure}",
                item.EventId,
                item.Revision,
                safeError);
        }
    }

    internal TimeSpan CalculateRetryDelay(int previousAttempts)
    {
        var exponent = Math.Min(previousAttempts, 10);
        var seconds = options.PollIntervalSeconds * (1 << exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, options.MaximumRetryDelaySeconds));
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, clock, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private static string Sanitize(string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 500 ? singleLine : singleLine[..500];
    }
}
