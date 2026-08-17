using System.Globalization;
using System.Text.Json;
using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Web;

internal static class BestConditionsEndpoints
{
    private const int MaximumPeriodDays = 180;
    private const double MinimumProductiveSpeedMpm = 10;
    private const double MinimumRunMinutes = 30;

    public static IEndpointRouteBuilder MapBestConditions(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/production/best-conditions", GetAsync)
            .RequireAuthorization();
        return endpoints;
    }

    internal static async Task<IResult> GetAsync(
        int? periodDays,
        string? productCode,
        double? grammageGsm,
        TimeProvider clock,
        HistorianRuntimeState runtimeState,
        ProductionIntegrationOptions productionOptions,
        IProductionIntegrationRepository productionRepository,
        IHistorianRepository historianRepository,
        CancellationToken cancellationToken)
    {
        var days = periodDays ?? 90;
        if (days is < 1 or > MaximumPeriodDays)
            return Results.BadRequest(new { error = $"O período deve estar entre 1 e {MaximumPeriodDays} dias." });
        if (string.IsNullOrWhiteSpace(productCode) != !grammageGsm.HasValue)
            return Results.BadRequest(new { error = "Informe produto e gramatura juntos." });
        if (productCode?.Length > 120)
            return Results.BadRequest(new { error = "O código do produto excede 120 caracteres." });
        if (grammageGsm.HasValue &&
            (!double.IsFinite(grammageGsm.Value) || grammageGsm.Value is <= 0 or > 10_000))
        {
            return Results.BadRequest(new { error = "A gramatura informada é inválida." });
        }

        var now = clock.GetUtcNow();
        var from = now.AddDays(-days);
        var productionState = await productionRepository.GetStateAsync(
            productionOptions.Provider,
            cancellationToken);
        var currentRun = productionState.CurrentRun;
        var currentProductCode = currentRun is { IsProducing: true, IsMixedQuality: false }
            ? Clean(currentRun.QualityProductCode)
            : null;
        var currentGrammage = currentRun is { IsProducing: true, IsMixedQuality: false }
            ? currentRun.QualityGrammageGsm
            : null;
        var currentQualityId = currentProductCode is not null && currentGrammage.HasValue
            ? BuildQualityId(currentProductCode, (double)currentGrammage.Value)
            : null;

        var qualityRows = await historianRepository.GetComparableProductionQualitiesAsync(
            now.AddDays(-MaximumPeriodDays),
            now,
            cancellationToken);
        var qualities = qualityRows
            .Select(row => new ComparableQualityResponse(
                BuildQualityId(row.ProductCode, row.GrammageGsm),
                row.ProductCode,
                row.ProductCode,
                row.GrammageGsm,
                row.ProductionPeriodCount,
                row.FirstObservedAtUtc,
                row.LastObservedAtUtc))
            .ToList();
        if (currentProductCode is not null && currentGrammage.HasValue &&
            qualities.All(quality => !string.Equals(quality.Id, currentQualityId, StringComparison.Ordinal)))
        {
            qualities.Insert(0, new ComparableQualityResponse(
                currentQualityId!,
                currentProductCode,
                currentProductCode,
                (double)currentGrammage.Value,
                0,
                currentRun!.FirstObservedAtUtc,
                currentRun.LastObservedAtUtc));
        }

        ComparableQualityResponse? selectedQuality;
        if (!string.IsNullOrWhiteSpace(productCode) && grammageGsm.HasValue)
        {
            selectedQuality = qualities.FirstOrDefault(quality =>
                string.Equals(quality.ProductCode, productCode.Trim(), StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(quality.GrammageGsm - grammageGsm.Value) < 0.001);
            if (selectedQuality is null)
                return Results.BadRequest(new { error = "Não há produção comparável para o produto e a gramatura informados." });
        }
        else
        {
            selectedQuality = qualities.FirstOrDefault(quality =>
                    string.Equals(quality.Id, currentQualityId, StringComparison.Ordinal)) ??
                qualities.FirstOrDefault();
        }

        IReadOnlyList<BestConditionRunAnalysis> analyses = [];
        if (selectedQuality is not null)
        {
            var samples = await historianRepository.GetBestConditionMinuteSamplesAsync(
                selectedQuality.ProductCode,
                selectedQuality.GrammageGsm,
                from,
                now,
                cancellationToken);
            analyses = BestConditionsAnalyzer.Analyze(
                samples,
                MinimumProductiveSpeedMpm,
                MinimumRunMinutes);
        }

        var currentSnapshot = runtimeState.GetSnapshot();
        var currentValues = ReadCurrentValues(
            currentSnapshot?.Status,
            currentSnapshot?.Commands);
        return Results.Ok(new BestConditionsResponse(
            now,
            currentQualityId,
            currentQualityId is null ? null : currentRun?.ProductionOrderCode,
            selectedQuality?.Id,
            currentValues,
            qualities,
            BestConditionsCatalog.Parameters.Select(parameter => new ParameterResponse(
                parameter.Key,
                parameter.Label,
                parameter.Category,
                parameter.Unit,
                parameter.Decimals)).ToArray(),
            analyses.Take(50).Select(analysis => new RunResponse(
                analysis.Id,
                BuildQualityId(analysis.ProductCode, analysis.GrammageGsm),
                analysis.ProductionOrderCode ?? $"Produção {analysis.ProductionRunId}",
                analysis.StartedAtUtc,
                analysis.EndedAtUtc,
                Math.Max(0, (int)Math.Floor((now - analysis.StartedAtUtc).TotalDays)),
                analysis.DurationMinutes,
                analysis.AverageSpeedMpm,
                analysis.MaximumSpeedMpm,
                analysis.SpeedVariationMpm,
                analysis.CoveragePct,
                analysis.Values,
                analysis.Ranges)).ToArray(),
            new MethodologyResponse(
                MinimumProductiveSpeedMpm,
                MinimumRunMinutes,
                "Presença efetiva de papel + velocidade mínima, com interrupção por perda de produção ou lacuna de telemetria.")));
    }

    private static IReadOnlyDictionary<string, double?> ReadCurrentValues(
        JsonElement? status,
        JsonElement? commands)
    {
        var numeric = status.HasValue && status.Value.ValueKind == JsonValueKind.Object
            ? TelemetryCatalog.Extract(status.Value, commands).Numeric
            : new Dictionary<string, double?>(StringComparer.Ordinal);
        return BestConditionsCatalog.Parameters.ToDictionary(
            parameter => parameter.Key,
            parameter => BestConditionsCatalog.CalculateValue(parameter, numeric),
            StringComparer.Ordinal);
    }

    private static string BuildQualityId(string productCode, double grammageGsm) =>
        $"{productCode.Trim().ToUpperInvariant()}|{grammageGsm.ToString("0.###", CultureInfo.InvariantCulture)}";

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record BestConditionsResponse(
        DateTimeOffset GeneratedAt,
        string? CurrentQualityId,
        string? CurrentOrderCode,
        string? SelectedQualityId,
        IReadOnlyDictionary<string, double?> CurrentValues,
        IReadOnlyList<ComparableQualityResponse> Qualities,
        IReadOnlyList<ParameterResponse> Parameters,
        IReadOnlyList<RunResponse> Runs,
        MethodologyResponse Methodology);

    private sealed record ComparableQualityResponse(
        string Id,
        string ProductCode,
        string ProductName,
        double GrammageGsm,
        int ProductionPeriodCount,
        DateTimeOffset FirstObservedAtUtc,
        DateTimeOffset LastObservedAtUtc);

    private sealed record ParameterResponse(
        string Key,
        string Label,
        string Category,
        string Unit,
        int Decimals);

    private sealed record RunResponse(
        string Id,
        string QualityId,
        string OrderCode,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt,
        int DaysAgo,
        double DurationMinutes,
        double AverageSpeedMpm,
        double MaximumSpeedMpm,
        double SpeedVariationMpm,
        double CoveragePct,
        IReadOnlyDictionary<string, double?> Values,
        IReadOnlyDictionary<string, BestConditionValueRange> Ranges);

    private sealed record MethodologyResponse(
        double MinimumProductiveSpeedMpm,
        double MinimumRunMinutes,
        string Description);
}
