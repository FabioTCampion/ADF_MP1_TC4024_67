using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Web;

public sealed class JumboWeightCaptureWorker(
    HistorianRuntimeState runtimeState,
    IHistorianRepository historianRepository,
    IProductionIntegrationRepository productionRepository,
    HistorianOptions historianOptions,
    ProductionIntegrationOptions productionOptions,
    TimeProvider clock,
    ILogger<JumboWeightCaptureWorker> logger) : BackgroundService
{
    private (long FileTime, long EventCounter)? _lastProcessedIdentity;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Jumbo weight capture worker started; PLC access remains read-only.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessCurrentSnapshotAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Jumbo weight capture processing failed; the retained PLC event will be retried.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(historianOptions.PollIntervalMilliseconds),
                    clock,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal async Task ProcessCurrentSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = runtimeState.GetSnapshot();
        if (snapshot is null ||
            !JumboWeightCaptureDetector.TryDetect(snapshot, out var detected) ||
            detected is null)
        {
            return;
        }

        var identity = (detected.CapturedAtFileTime, detected.PlcEventCounter);
        if (_lastProcessedIdentity == identity)
            return;

        var production = await TryResolveProductionContextAsync(
            detected.CapturedAtUtc,
            cancellationToken);
        var capture = detected with { Production = production };
        var inserted = await historianRepository.AddJumboWeightCaptureAsync(
            capture,
            cancellationToken);
        _lastProcessedIdentity = identity;

        if (inserted)
        {
            logger.LogInformation(
                "Jumbo weight captured: {WeightKg} kg at {CapturedAtUtc}; PLC event {EventCounter}, production run {ProductionRunId}.",
                capture.WeightKg,
                capture.CapturedAtUtc,
                capture.PlcEventCounter,
                capture.Production?.RunId);
        }
    }

    private async Task<JumboWeightProductionContext?> TryResolveProductionContextAsync(
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!productionOptions.Enabled)
            return null;

        try
        {
            var state = await productionRepository.GetStateAsync(
                productionOptions.Provider,
                cancellationToken);
            var currentRun = state.CurrentRun;
            if (currentRun is null ||
                !currentRun.IsProducing ||
                !state.LastSuccessfulSyncAtUtc.HasValue ||
                capturedAtUtc < currentRun.FirstObservedAtUtc ||
                capturedAtUtc > state.LastSuccessfulSyncAtUtc.Value.AddSeconds(
                    productionOptions.StaleAfterSeconds) ||
                clock.GetUtcNow() - state.LastSuccessfulSyncAtUtc.Value >
                    TimeSpan.FromSeconds(productionOptions.StaleAfterSeconds))
            {
                return null;
            }

            return new JumboWeightProductionContext(
                currentRun.Id,
                currentRun.SourceSystem,
                currentRun.ExternalRunId,
                currentRun.ProductionOrderCode,
                currentRun.QualityKey,
                currentRun.QualityProductCode,
                currentRun.QualityGrammageGsm,
                currentRun.ProductionWidthMm,
                state.LastSuccessfulSyncAtUtc.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "ERP context could not be associated with the jumbo weight; the weight will still be saved.");
            return null;
        }
    }
}
