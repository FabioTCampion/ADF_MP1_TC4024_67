using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;
using PaperMachine.Historian.Infrastructure.Ads;

namespace PaperMachine.Historian.Web;

public sealed class HistorianWorker(
    IPaperMachineReader reader,
    IHistorianRepository repository,
    HistorianProcessor processor,
    HistorianRuntimeState runtimeState,
    HistorianOptions historianOptions,
    AdsOptions adsOptions,
    ILogger<HistorianWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = adsOptions.ReconnectMinimumDelayMilliseconds;
        var communicationState = "Starting";

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!reader.IsConnected)
                {
                    await reader.ConnectAsync(stoppingToken);
                    await RecordCommunicationEventAsync("Connected", null, stoppingToken);
                    logger.LogInformation(
                        "Connected read-only ADS client to {AmsNetId}:{Port}.",
                        adsOptions.AmsNetId,
                        adsOptions.Port);
                    communicationState = "Connected";
                    retryDelay = adsOptions.ReconnectMinimumDelayMilliseconds;
                }

                var snapshot = await reader.ReadSnapshotAsync(stoppingToken);
                var cycle = processor.Process(snapshot);
                await repository.PersistCycleAsync(cycle, stoppingToken);
                runtimeState.SetConnected(snapshot);

                await Task.Delay(historianOptions.PollIntervalMilliseconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                runtimeState.SetDisconnected(exception.Message);
                logger.LogWarning(
                    exception,
                    "ADS historian cycle failed. The application remains read-only and will reconnect.");

                if (!string.Equals(communicationState, "Faulted", StringComparison.Ordinal))
                {
                    await TryRecordFailureAsync(exception, stoppingToken);
                    communicationState = "Faulted";
                }

                try
                {
                    await reader.DisconnectAsync(stoppingToken);
                }
                catch (Exception disconnectException)
                {
                    logger.LogDebug(disconnectException, "ADS disconnect after failure did not complete cleanly.");
                }

                var jitter = Random.Shared.Next(0, Math.Max(1, retryDelay / 5));
                await Task.Delay(retryDelay + jitter, stoppingToken);
                retryDelay = Math.Min(retryDelay * 2, adsOptions.ReconnectMaximumDelayMilliseconds);
            }
        }

        try
        {
            await reader.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "ADS disconnect during service shutdown did not complete cleanly.");
        }
    }

    private async Task TryRecordFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            await RecordCommunicationEventAsync("Faulted", exception.Message, cancellationToken);
        }
        catch (Exception persistenceException)
        {
            logger.LogError(
                persistenceException,
                "Could not persist the ADS communication failure.");
        }
    }

    private Task RecordCommunicationEventAsync(
        string state,
        string? detail,
        CancellationToken cancellationToken) =>
        repository.AddCommunicationEventAsync(
            new CommunicationEvent(
                DateTimeOffset.UtcNow,
                state,
                detail,
                adsOptions.AmsNetId,
                adsOptions.Port),
            cancellationToken);
}
