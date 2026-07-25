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
        var commandPersistenceTask = PersistCommandChangesAsync(stoppingToken);
        var nextMaintenanceAtUtc =
            DateTimeOffset.UtcNow.AddMinutes(Math.Min(1, historianOptions.MaintenanceIntervalMinutes));

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
                var cycle = processor.Process(snapshot) with { CommandChanges = [] };
                await repository.PersistCycleAsync(cycle, stoppingToken);
                runtimeState.SetConnected(snapshot);

                if (snapshot.CapturedAtUtc >= nextMaintenanceAtUtc)
                {
                    await TryRunMaintenanceAsync(snapshot.CapturedAtUtc, stoppingToken);
                    nextMaintenanceAtUtc = snapshot.CapturedAtUtc.AddMinutes(
                        historianOptions.MaintenanceIntervalMinutes);
                }

                await Task.Delay(historianOptions.PollIntervalMilliseconds, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

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

        try
        {
            await commandPersistenceTask;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task PersistCommandChangesAsync(CancellationToken cancellationToken)
    {
        await foreach (var change in reader.ReadCommandChangesAsync(cancellationToken))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await repository.AddCommandEventAsync(
                        change,
                        historianOptions.MappingVersion,
                        cancellationToken);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Could not persist ADS on-change command {CommandName}; retrying.",
                        change.FieldName);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
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

    private async Task TryRunMaintenanceAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await repository.RunMaintenanceAsync(
                nowUtc,
                historianOptions,
                cancellationToken);
            var deleted = result.DeletedDiagnosticSnapshots +
                result.DeletedRawTelemetrySamples +
                result.DeletedMinuteAggregates +
                result.DeletedStatusChanges +
                result.DeletedAnalogStatusChanges +
                result.DeletedCommunicationEvents;
            if (deleted > 0)
            {
                logger.LogInformation(
                    "Historian maintenance deleted {DeletedRows} expired rows in small batches.",
                    deleted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Historian maintenance failed; PLC acquisition will continue.");
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
