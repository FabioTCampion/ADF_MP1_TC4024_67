using System.Text.Json;

namespace PaperMachine.Historian.Application;

public static class MotorTrendBuilder
{
    public static MotorTrend Build(IReadOnlyList<StatusSnapshotRow> rows)
    {
        var parsedRows = rows
            .Select(row => new ParsedStatusRow(row.CapturedAtUtc, ParseNumbers(row.PayloadJson)))
            .ToArray();
        var allFieldNames = parsedRows
            .SelectMany(row => row.Values.Keys)
            .ToHashSet(StringComparer.Ordinal);
        var motors = allFieldNames
            .Where(field => field.EndsWith("Torque", StringComparison.Ordinal))
            .Select(torqueField => CreateDefinition(torqueField, allFieldNames))
            .Where(definition => definition is not null)
            .Select(definition => definition!)
            .OrderBy(definition => definition.Key, StringComparer.Ordinal)
            .ToArray();
        var samples = parsedRows
            .Select(row => new MotorTrendSample(
                row.CapturedAtUtc,
                motors.ToDictionary(
                    motor => motor.Key,
                    motor => new MotorTrendValue(
                        row.Values.GetValueOrDefault(motor.SpeedField),
                        row.Values.GetValueOrDefault(motor.TorqueField)),
                    StringComparer.Ordinal)))
            .ToArray();

        return new MotorTrend(motors, samples);
    }

    private static MotorTrendMotor? CreateDefinition(
        string torqueField,
        IReadOnlySet<string> allFieldNames)
    {
        var key = torqueField[..^"Torque".Length];
        var speedMpmField = $"{key}SpeedMPM";
        var speedField = allFieldNames.Contains(speedMpmField)
            ? speedMpmField
            : $"{key}Speed";
        if (!allFieldNames.Contains(speedField))
            return null;

        return new MotorTrendMotor(
            key,
            speedField,
            torqueField,
            speedField.EndsWith("SpeedMPM", StringComparison.Ordinal) ? "m/min" : "PLC",
            "PLC");
    }

    private static Dictionary<string, double?> ParseNumbers(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var values = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetDouble(out var value))
            {
                values[property.Name] = value;
            }
        }
        return values;
    }

    private sealed record ParsedStatusRow(
        DateTimeOffset CapturedAtUtc,
        IReadOnlyDictionary<string, double?> Values);
}

public sealed record MotorTrend(
    IReadOnlyList<MotorTrendMotor> Motors,
    IReadOnlyList<MotorTrendSample> Samples);

public sealed record MotorTrendMotor(
    string Key,
    string SpeedField,
    string TorqueField,
    string SpeedUnit,
    string TorqueUnit);

public sealed record MotorTrendSample(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyDictionary<string, MotorTrendValue> Values);

public sealed record MotorTrendValue(double? Speed, double? Torque);
