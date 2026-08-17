namespace PaperMachine.Historian.Application;

public static class BestConditionsCatalog
{
    public static IReadOnlyList<BestConditionParameterDefinition> Parameters { get; } =
    [
        Parameter("pressDraw", "Estiramento da prensa", "traction", "%", 2, "presseGroupSpeedDif"),
        Parameter("drying1Draw", "Estiramento secagem G1", "traction", "%", 2, "dryingSectionGroup1SpeedDif"),
        Parameter("drying2Draw", "Estiramento secagem G2", "traction", "%", 2, "dryingSectionGroup2SpeedDif"),
        Parameter("drying3Draw", "Estiramento secagem G3", "traction", "%", 2, "dryingSectionGroup3SpeedDif"),
        Parameter("stockFlow", "Vazão de massa", "stock", "m³/h", 1, "stockPumpFlowM3h"),
        Parameter("dryMass", "Massa seca alimentada", "stock", "kg/h", 0, "stockPumpDryMassFeedbackKgH"),
        Parameter("consistency", "Consistência utilizada", "stock", "%", 2, "stockPumpConsistencyUsedPct"),
        Parameter("stockTank", "Nível do tanque de massa", "stock", "%", 1, "stockTankLevel"),
        Parameter("steam1", "Pressão de vapor G1", "drying", "bar", 2, "dryingSectionGroup1SteamPressure"),
        Parameter("steam2", "Pressão de vapor G2", "drying", "bar", 2, "dryingSectionGroup2SteamPressure"),
        Parameter("steam3", "Pressão de vapor G3", "drying", "bar", 2, "dryingSectionGroup3SteamPressure"),
        Parameter("headboxPressure", "Pressão da caixa de entrada", "headbox", "mmH₂O", 0, "headBoxMMH2O"),
        Parameter("headboxLips", "Abertura dos lábios", "headbox", "mm", 2, "headboxLipsPosition_mm"),
        Parameter("jetWireRatio", "Relação jato/tela", "headbox", "%", 2, "mixPumpRatio"),
        Parameter("whiteWater", "Nível do silo de água branca", "finishing", "%", 1, "whiteWaterSiloLevel"),
        Parameter(
            "winderPressure",
            "Pressão média da enroladeira",
            "finishing",
            "bar",
            2,
            "winderPressureSetpointOperatorSide",
            "winderPressureSetpointDriveSide")
    ];

    public static IReadOnlyList<string> TelemetryFields { get; } = Parameters
        .SelectMany(parameter => parameter.SourceFields)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    // These fields existed in status snapshots before becoming optimized telemetry.
    public static IReadOnlySet<string> LegacySnapshotFallbackFields { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "presseGroupSpeedDif",
            "dryingSectionGroup1SpeedDif",
            "dryingSectionGroup2SpeedDif",
            "dryingSectionGroup3SpeedDif",
            "headBoxMMH2O",
            "headboxLipsPosition_mm",
            "mixPumpRatio",
            "whiteWaterSiloLevel",
            "winderPressureSetpointOperatorSide",
            "winderPressureSetpointDriveSide"
        };

    public static double? CalculateValue(
        BestConditionParameterDefinition parameter,
        IReadOnlyDictionary<string, double?> numericValues)
    {
        var values = parameter.SourceFields
            .Select(field => numericValues.GetValueOrDefault(field))
            .Where(value => value.HasValue && double.IsFinite(value.Value))
            .Select(value => value!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static BestConditionParameterDefinition Parameter(
        string key,
        string label,
        string category,
        string unit,
        int decimals,
        params string[] sourceFields) =>
        new(key, label, category, unit, decimals, sourceFields);
}

public sealed record BestConditionParameterDefinition(
    string Key,
    string Label,
    string Category,
    string Unit,
    int Decimals,
    IReadOnlyList<string> SourceFields);
