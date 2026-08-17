namespace PaperMachine.Historian.Application;

public static class BestConditionsCatalog
{
    public const double ActiveDriveMinimumSpeedMpm = 10;

    public static IReadOnlyList<BestConditionParameterDefinition> Parameters { get; } =
    [
        Parameter("stockFlow", "Fluxo de massa", "stock", "m³/h", 1, "stockPumpFlowM3h"),
        Parameter("headboxPressure", "Pressão da caixa de entrada", "headbox", "mmH₂O", 0, "headBoxMMH2O"),
        Parameter("jetWireRatio", "Ratio da bomba de mistura", "headbox", "", 3, TelemetryCatalog.MixPumpRatioField),
        Parameter("headboxLips", "Abertura do lábio", "headbox", "mm", 2, "headboxLipsPosition_mm"),
        Parameter("stockPumpSpeed", "Velocidade da bomba de massa", "stock", "%", 1, "stockPumpSpeed"),
        Parameter("suctionRollTorque", "Torque do rolo de sucção", "forming", "%", 1, "formingBoardSuctionRollTorque"),
        Parameter("tractionRollTorque", "Torque do rolo de tração", "forming", "%", 1, "formingBoardTractionRollTorque"),
        Parameter("steam1", "Pressão de vapor G1", "drying", "bar", 2, "dryingSectionGroup1SteamPressure"),
        Parameter("steam2", "Pressão de vapor G2", "drying", "bar", 2, "dryingSectionGroup2SteamPressure"),
        Parameter("steam3", "Pressão de vapor G3", "drying", "bar", 2, "dryingSectionGroup3SteamPressure"),
        Parameter("pressDraw", "Passe da prensa", "traction", "%", 2, "presseGroupSpeedDif"),
        Parameter("drying1Draw", "Passe da secagem G1", "traction", "%", 2, "dryingSectionGroup1SpeedDif"),
        Parameter("drying2Draw", "Passe da secagem G2", "traction", "%", 2, "dryingSectionGroup2SpeedDif"),
        Parameter("drying3Draw", "Passe da secagem G3", "traction", "%", 2, "dryingSectionGroup3SpeedDif"),
        ActiveDriveAverage(
            "drying1Torque",
            "Torque médio da secagem G1",
            ("dryingSectionGroup1UpperMasterTorque", "dryingSectionGroup1UpperMasterSpeedMPM"),
            ("dryingSectionGroup1UpperSlave1Torque", "dryingSectionGroup1UpperSlave1SpeedMPM"),
            ("dryingSectionGroup1UpperSlave2Torque", "dryingSectionGroup1UpperSlave2SpeedMPM"),
            ("dryingSectionGroup1LowerMasterTorque", "dryingSectionGroup1LowerMasterSpeedMPM"),
            ("dryingSectionGroup1LowerSlave1Torque", "dryingSectionGroup1LowerSlave1SpeedMPM"),
            ("dryingSectionGroup1LowerSlave2Torque", "dryingSectionGroup1LowerSlave2SpeedMPM")),
        ActiveDriveAverage(
            "drying2Torque",
            "Torque médio da secagem G2",
            ("dryingSectionGroup2UpperMasterTorque", "dryingSectionGroup2UpperMasterSpeedMPM"),
            ("dryingSectionGroup2UpperSlave1Torque", "dryingSectionGroup2UpperSlave1SpeedMPM"),
            ("dryingSectionGroup2UpperSlave2Torque", "dryingSectionGroup2UpperSlave2Speed"),
            ("dryingSectionGroup2LowerMasterTorque", "dryingSectionGroup2LowerMasterSpeedMPM"),
            ("dryingSectionGroup2LowerSlave1Torque", "dryingSectionGroup2LowerSlave1SpeedMPM"),
            ("dryingSectionGroup2LowerSlave2Torque", "dryingSectionGroup2LowerSlave2SpeedMPM")),
        ActiveDriveAverage(
            "drying3Torque",
            "Torque médio da secagem G3",
            ("dryingSectionGroup3UpperMasterTorque", "dryingSectionGroup3UpperMasterSpeedMPM"),
            ("dryingSectionGroup3UpperSlave1Torque", "dryingSectionGroup3UpperSlave1SpeedMPM"),
            ("dryingSectionGroup3UpperSlave2Torque", "dryingSectionGroup3UpperSlave2SpeedMPM"),
            ("dryingSectionGroup3LowerMasterTorque", "dryingSectionGroup3LowerMasterSpeedMPM"),
            ("dryingSectionGroup3LowerSlave1Torque", "dryingSectionGroup3LowerSlave1SpeedMPM"),
            ("dryingSectionGroup3LowerSlave2Torque", "dryingSectionGroup3LowerSlave2SpeedMPM"))
    ];

    public static IReadOnlyList<string> TelemetryFields { get; } = Parameters
        .SelectMany(parameter => parameter.SourceFields.Concat(parameter.ActivityFields))
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
            TelemetryCatalog.MixPumpRatioField
        };

    public static double? CalculateValue(
        BestConditionParameterDefinition parameter,
        IReadOnlyDictionary<string, double?> numericValues)
    {
        if (parameter.ActivityFields.Count == 0)
            return AverageFinite(parameter.SourceFields, numericValues);

        var activeValues = parameter.SourceFields
            .Select((field, index) => new
            {
                Value = numericValues.GetValueOrDefault(field),
                Speed = numericValues.GetValueOrDefault(parameter.ActivityFields[index])
            })
            .Where(item =>
                item.Value.HasValue && double.IsFinite(item.Value.Value) &&
                item.Speed.HasValue && double.IsFinite(item.Speed.Value) &&
                Math.Abs(item.Speed.Value) >= ActiveDriveMinimumSpeedMpm)
            .Select(item => item.Value!.Value)
            .ToArray();
        return activeValues.Length == 0 ? null : activeValues.Average();
    }

    private static double? AverageFinite(
        IReadOnlyList<string> fields,
        IReadOnlyDictionary<string, double?> numericValues)
    {
        var values = fields
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
        new(key, label, category, unit, decimals, sourceFields, []);

    private static BestConditionParameterDefinition ActiveDriveAverage(
        string key,
        string label,
        params (string Torque, string Speed)[] drives) =>
        new(
            key,
            label,
            "drying",
            "%",
            1,
            drives.Select(drive => drive.Torque).ToArray(),
            drives.Select(drive => drive.Speed).ToArray());
}

public sealed record BestConditionParameterDefinition(
    string Key,
    string Label,
    string Category,
    string Unit,
    int Decimals,
    IReadOnlyList<string> SourceFields,
    IReadOnlyList<string> ActivityFields);
