namespace PaperMachine.Historian.Web;

internal sealed class UpdateCheckWorker(
    ApplicationUpdateService updates,
    UpdateOptions options,
    ILogger<UpdateCheckWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Automatic GitHub update checks are disabled.");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(options.CheckIntervalMinutes));
        do
        {
            try
            {
                await updates.CheckAsync(options.AutoDownload, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Automatic GitHub update check failed; historian acquisition continues.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
