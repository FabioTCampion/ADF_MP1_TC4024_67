using System.Text.Json;

namespace PaperMachine.Historian.Application;

public static class ProcessTrendBuilder
{
    public static ProcessTrend Build(
        IReadOnlyList<string> requestedFields,
        IReadOnlyList<TelemetrySampleRow> telemetryRows,
        IReadOnlyList<StatusSnapshotRow> snapshotRows)
    {
        var numericTelemetryFields = TelemetryCatalog.NumericFields.ToHashSet(StringComparer.Ordinal);
        var booleanTelemetryFields = TelemetryCatalog.BooleanFields.ToHashSet(StringComparer.Ordinal);
        var snapshots = snapshotRows.Select(ParseSnapshot).ToArray();

        var series = requestedFields.Select(field =>
        {
            var definition = snapshots.Any(row => row.BooleanFields.Contains(field))
                ? new ProcessVariableDefinition(field, "Estado", "0/1")
                : ProcessVariableCatalog.Describe(field);
            IReadOnlyList<ProcessTrendPoint> points;
            if (numericTelemetryFields.Contains(field))
            {
                points = telemetryRows
                    .Select(row => new ProcessTrendPoint(
                        row.CapturedAtUtc,
                        row.NumericValues.GetValueOrDefault(field)))
                    .Where(point => point.Value.HasValue)
                    .ToArray();
            }
            else if (booleanTelemetryFields.Contains(field))
            {
                points = telemetryRows
                    .Select(row => new ProcessTrendPoint(
                        row.CapturedAtUtc,
                        row.BooleanValues.GetValueOrDefault(field) is bool value
                            ? value ? 1d : 0d
                            : null))
                    .Where(point => point.Value.HasValue)
                    .ToArray();
            }
            else
            {
                points = snapshots
                    .Select(row => new ProcessTrendPoint(
                        row.CapturedAtUtc,
                        row.Values.GetValueOrDefault(field)))
                    .Where(point => point.Value.HasValue)
                    .ToArray();
            }

            return new ProcessTrendSeries(
                definition.FieldName,
                definition.Category,
                definition.Unit,
                points);
        }).ToArray();

        return new ProcessTrend(series);
    }

    private static ParsedProcessSnapshot ParseSnapshot(StatusSnapshotRow row)
    {
        using var document = JsonDocument.Parse(row.PayloadJson);
        var values = new Dictionary<string, double?>(StringComparer.Ordinal);
        var booleanFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetDouble(out var number) &&
                double.IsFinite(number))
            {
                values[property.Name] = number;
            }
            else if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                values[property.Name] = property.Value.GetBoolean() ? 1d : 0d;
                booleanFields.Add(property.Name);
            }
        }
        return new ParsedProcessSnapshot(row.CapturedAtUtc, values, booleanFields);
    }

    private sealed record ParsedProcessSnapshot(
        DateTimeOffset CapturedAtUtc,
        IReadOnlyDictionary<string, double?> Values,
        IReadOnlySet<string> BooleanFields);
}

public sealed record ProcessTrend(
    IReadOnlyList<ProcessTrendSeries> Series);

public sealed record ProcessTrendSeries(
    string FieldName,
    string Category,
    string Unit,
    IReadOnlyList<ProcessTrendPoint> Points);

public sealed record ProcessTrendPoint(
    DateTimeOffset CapturedAtUtc,
    double? Value);
