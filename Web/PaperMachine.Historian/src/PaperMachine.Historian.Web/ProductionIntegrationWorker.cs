using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Web;

internal sealed class ProductionIntegrationWorker(
    ProductionIntegrationOptions options,
    IProductionSourceClient client,
    IProductionIntegrationRepository repository,
    TimeProvider clock,
    ILogger<ProductionIntegrationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Production integration is disabled.");
            return;
        }

        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var observation = await client.ReadAsync(stoppingToken);
                await repository.ApplyObservationAsync(observation, stoppingToken);
                consecutiveFailures = 0;
                logger.LogDebug(
                    "Production integration synchronized run {ExternalRunId} from {SourceSystem}.",
                    observation.ExternalRunId,
                    observation.SourceSystem);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                var safeMessage = Sanitize(exception.Message);
                try
                {
                    await repository.RecordFailureAsync(
                        client.SourceSystem,
                        clock.GetUtcNow(),
                        safeMessage,
                        stoppingToken);
                }
                catch (Exception persistenceException)
                {
                    logger.LogError(
                        persistenceException,
                        "Could not persist the production integration failure state.");
                }
                logger.LogWarning(
                    "Production integration attempt failed ({FailureCount}): {Failure}",
                    consecutiveFailures,
                    safeMessage);
            }

            var multiplier = consecutiveFailures == 0
                ? 1
                : Math.Min(5, 1 << Math.Min(2, consecutiveFailures - 1));
            var delay = TimeSpan.FromSeconds(options.PollIntervalSeconds * multiplier);
            try
            {
                await Task.Delay(delay, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static string Sanitize(string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 500 ? singleLine : singleLine[..500];
    }
}
