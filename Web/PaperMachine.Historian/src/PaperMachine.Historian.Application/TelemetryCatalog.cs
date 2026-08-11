using System.Text.Json;

namespace PaperMachine.Historian.Application;

public static class TelemetryCatalog
{
    public const string MachineSpeedField = "dryingSectionGroup3UpperMasterSpeedMPM";
    public const string PaperPresenceField = "dryingSectionGroup3PaperPresence";
    public const string StockPumpStateField = "stockPumpState";
    public const int StockPumpRunningState = 1;
    public const string EffectivePaperPresenceField = "effectivePaperPresence";

    public static IReadOnlyList<TelemetryMotorDefinition> Motors { get; } =
    [
        Motor("formingBoardSuctionRoll", "formingBoardSuctionRollSpeed"),
        Motor("formingBoardTractionRoll", "formingBoardTractionRollSpeed"),
        Motor("firstPressSection", "firstPressSectionSpeed"),
        Motor("secondPressSection", "secondPressSectionSpeed"),
        Motor("dryingSectionGroup1UpperMaster", "dryingSectionGroup1UpperMasterSpeedMPM"),
        Motor("dryingSectionGroup1UpperSlave1", "dryingSectionGroup1UpperSlave1SpeedMPM"),
        Motor("dryingSectionGroup1UpperSlave2", "dryingSectionGroup1UpperSlave2SpeedMPM"),
        Motor("dryingSectionGroup1LowerMaster", "dryingSectionGroup1LowerMasterSpeedMPM"),
        Motor("dryingSectionGroup1LowerSlave1", "dryingSectionGroup1LowerSlave1SpeedMPM"),
        Motor("dryingSectionGroup1LowerSlave2", "dryingSectionGroup1LowerSlave2SpeedMPM"),
        Motor("dryingSectionGroup2UpperMaster", "dryingSectionGroup2UpperMasterSpeedMPM"),
        Motor("dryingSectionGroup2UpperSlave1", "dryingSectionGroup2UpperSlave1SpeedMPM"),
        Motor("dryingSectionGroup2UpperSlave2", "dryingSectionGroup2UpperSlave2Speed"),
        Motor("dryingSectionGroup2LowerMaster", "dryingSectionGroup2LowerMasterSpeedMPM"),
        Motor("dryingSectionGroup2LowerSlave1", "dryingSectionGroup2LowerSlave1SpeedMPM"),
        Motor("dryingSectionGroup2LowerSlave2", "dryingSectionGroup2LowerSlave2SpeedMPM"),
        Motor("dryingSectionGroup3UpperMaster", "dryingSectionGroup3UpperMasterSpeedMPM"),
        Motor("dryingSectionGroup3UpperSlave1", "dryingSectionGroup3UpperSlave1SpeedMPM"),
        Motor("dryingSectionGroup3UpperSlave2", "dryingSectionGroup3UpperSlave2SpeedMPM"),
        Motor("dryingSectionGroup3LowerMaster", "dryingSectionGroup3LowerMasterSpeedMPM"),
        Motor("dryingSectionGroup3LowerSlave1", "dryingSectionGroup3LowerSlave1SpeedMPM"),
        Motor("dryingSectionGroup3LowerSlave2", "dryingSectionGroup3LowerSlave2SpeedMPM"),
        Motor("couchPitPump", "couchPitPumpSpeed", isPump: true),
        Motor("wirePitPump", "wirePitPumpSpeed", isPump: true),
        Motor("stockPump", "stockPumpSpeed", isPump: true),
        Motor("mixPump", "mixPumpSpeed", isPump: true),
        Motor("winder", "winderSpeedMPM")
    ];

    public static IReadOnlyList<TelemetrySteamDefinition> SteamPressures { get; } =
    [
        new("drying-1", "Grupo 1", "dryingSectionGroup1SteamPressure", "bar"),
        new("drying-2", "Grupo 2", "dryingSectionGroup2SteamPressure", "bar"),
        new("drying-3", "Grupo 3", "dryingSectionGroup3SteamPressure", "bar")
    ];

    public static IReadOnlyList<string> PaperPresenceFields { get; } =
    [
        "dryingSectionGroup1PaperPresence",
        "dryingSectionGroup2PaperPresence",
        PaperPresenceField,
        "winderPaperPresence"
    ];

    public static IReadOnlyList<string> StockPumpNumericFields { get; } =
    [
        "stockTankLevel",
        "stockPumpFlowM3h",
        "stockPumpSpeedReferencePct",
        "stockPumpDryMassSetpointKgH",
        "stockPumpDryMassFeedbackKgH",
        "stockPumpFlowTheoreticalM3h",
        "stockPumpFlowCorrectedM3h",
        "stockPumpFlowLimitedM3h",
        "stockPumpFlowSetpointM3h",
        "stockPumpConsistencyFilteredPct",
        "stockPumpConsistencyUsedPct",
        "refinedStockTankConsistencyFilteredPct",
        "stockPumpPidErrorM3h",
        "stockPumpPidOutputPct",
        "stockPumpSuggestedCalibrationFactor",
        "stockPumpCurrent"
    ];

    public static IReadOnlyList<string> StockPumpBooleanFields { get; } =
    [
        "stockPumpVfdStartCmd",
        "stockPumpVfdResetCmd",
        "refinedStockTankConsistencySignalInvalid",
        "stockTankLevelSignalInvalid",
        "stockPumpPidOutputSaturated",
        "stockPumpSuggestedCalibrationFactorValid",
        "stockPumpFlowCalculationValid",
        "stockPumpAutomaticControlValid",
        "stockPumpAutomaticControlInvalid",
        "stockPumpConsistencySignalInvalid",
        "stockPumpFlowSignalInvalid",
        "stockPumpBasisWeightSetpointInvalid",
        "stockPumpEffectiveWidthInvalid",
        "stockPumpWireSpeedInvalid",
        "stockPumpCalibrationFactorInvalid",
        "stockPumpOperatorTrimFactorInvalid",
        "stockPumpFlowSetpointLimited",
        "stockPumpSpeedReferenceLimited",
        "stockPumpUsingLastValidConsistency",
        "stockPumpFlowDeviationWarning",
        "stockPumpFlowDeviationAlarm",
        "stockPumpWarningActive",
        "stockPumpAlarmActive",
        "stockPumpRunning",
        "stockPumpControlActive",
        "stockPumpManualActive",
        "stockPumpAutomaticActive"
    ];

    public static IReadOnlyList<string> BooleanFields { get; } = PaperPresenceFields
        .Append(EffectivePaperPresenceField)
        .Concat(StockPumpBooleanFields)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<string> NumericFields { get; } = Motors
        .SelectMany(motor => new[] { motor.SpeedField, motor.TorqueField })
        .Concat(SteamPressures.Select(item => item.Field))
        .Concat(StockPumpNumericFields)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public static TelemetryValues Extract(JsonElement status)
    {
        var properties = status.EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var numeric = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var field in NumericFields)
        {
            numeric[field] =
                properties.TryGetValue(field, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetDouble(out var number)
                    ? number
                    : null;
        }

        var boolean = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var field in BooleanFields.Where(field =>
                     field != EffectivePaperPresenceField))
        {
            boolean[field] =
                properties.TryGetValue(field, out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : null;
        }
        boolean[EffectivePaperPresenceField] = TryReadEffectivePaperPresence(properties);

        return new TelemetryValues(numeric, boolean);
    }

    public static bool? TryReadEffectivePaperPresence(JsonElement status)
    {
        var properties = status.EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
        return TryReadEffectivePaperPresence(properties);
    }

    private static bool? TryReadEffectivePaperPresence(
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        var sensorPresent =
            properties.TryGetValue(PaperPresenceField, out var sensorValue) &&
            sensorValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? sensorValue.GetBoolean()
                : (bool?)null;
        var stockPumpState =
            properties.TryGetValue(StockPumpStateField, out var pumpValue) &&
            pumpValue.ValueKind == JsonValueKind.Number &&
            pumpValue.TryGetInt32(out var state)
                ? state
                : (int?)null;

        if (sensorPresent == false || stockPumpState is not null and not StockPumpRunningState)
            return false;

        return sensorPresent == true && stockPumpState == StockPumpRunningState
            ? true
            : null;
    }

    public static bool IsDiscreteStatusField(string fieldName, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.String)
            return true;

        return fieldName.EndsWith("State", StringComparison.OrdinalIgnoreCase) ||
               fieldName.EndsWith("FaultCode", StringComparison.OrdinalIgnoreCase) ||
               fieldName.EndsWith("FaultEventCounter", StringComparison.OrdinalIgnoreCase);
    }

    private static TelemetryMotorDefinition Motor(
        string key,
        string speedField,
        bool isPump = false) =>
        new(
            key,
            speedField,
            $"{key}Torque",
            isPump ? "%" : "m/min",
            "%");
}

public sealed record TelemetryMotorDefinition(
    string Key,
    string SpeedField,
    string TorqueField,
    string SpeedUnit,
    string TorqueUnit);

public sealed record TelemetrySteamDefinition(
    string Key,
    string Label,
    string Field,
    string Unit);

public sealed record TelemetryValues(
    IReadOnlyDictionary<string, double?> Numeric,
    IReadOnlyDictionary<string, bool?> Boolean);
