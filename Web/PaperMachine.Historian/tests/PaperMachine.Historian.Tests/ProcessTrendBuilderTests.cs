using PaperMachine.Historian.Application;
using System.Text.Json;

namespace PaperMachine.Historian.Tests;

public sealed class ProcessTrendBuilderTests
{
    [Fact]
    public void ExtractsAndCatalogsNewStockPumpProcessVariables()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "stockTankLevel": 73.4,
              "stockPumpFlowM3h": 42.8,
              "stockPumpDryMassFeedbackKgH": 3180.5,
              "stockPumpConsistencyFilteredPct": 3.72,
              "stockPumpPidOutputPct": 56.2,
              "stockPumpAutomaticActive": true,
              "stockPumpFlowDeviationAlarm": false
            }
            """);

        var values = TelemetryCatalog.Extract(document.RootElement);

        Assert.Equal(73.4, values.Numeric["stockTankLevel"]);
        Assert.Equal(42.8, values.Numeric["stockPumpFlowM3h"]);
        Assert.Equal(3180.5, values.Numeric["stockPumpDryMassFeedbackKgH"]);
        Assert.Equal(3.72, values.Numeric["stockPumpConsistencyFilteredPct"]);
        Assert.Equal(56.2, values.Numeric["stockPumpPidOutputPct"]);
        Assert.True(values.Boolean["stockPumpAutomaticActive"]);
        Assert.False(values.Boolean["stockPumpFlowDeviationAlarm"]);
        Assert.Equal(15, TelemetryCatalog.StockPumpNumericFields.Count);
        Assert.Equal(26, TelemetryCatalog.StockPumpBooleanFields.Count);
        Assert.True(ProcessVariableCatalog.IsSupportedFieldName(
            "stockPumpDryMassFeedbackKgH"));
    }

    [Fact]
    public void CombinesOptimizedTelemetryAndDetailedSnapshotsInRequestedOrder()
    {
        var capturedAt = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        var speedField = TelemetryCatalog.MachineSpeedField;
        var telemetry = new[]
        {
            new TelemetrySampleRow(
                capturedAt,
                new Dictionary<string, double?> { [speedField] = 132.5 },
                new Dictionary<string, bool?>(),
                "test-v1",
                "Good")
        };
        var snapshots = new[]
        {
            new StatusSnapshotRow(
                1,
                capturedAt,
                """{"headBoxMMH2O":348.2,"customBoolean":true}""",
                "test-v1",
                "Good")
        };

        var trend = ProcessTrendBuilder.Build(
            [speedField, "headBoxMMH2O", "customBoolean"],
            telemetry,
            snapshots);

        Assert.Equal(
            [speedField, "headBoxMMH2O", "customBoolean"],
            trend.Series.Select(series => series.FieldName));
        Assert.Equal(132.5, Assert.Single(trend.Series[0].Points).Value);
        Assert.Equal("m/min", trend.Series[0].Unit);
        Assert.Equal(348.2, Assert.Single(trend.Series[1].Points).Value);
        Assert.Equal("mmH₂O", trend.Series[1].Unit);
        Assert.Equal(1, Assert.Single(trend.Series[2].Points).Value);
        Assert.Equal("0/1", trend.Series[2].Unit);
    }

    [Theory]
    [InlineData("stockPumpSpeed", "Bombas", "%")]
    [InlineData("firstPressSectionTorque", "Torque", "%")]
    [InlineData("dryerTemperature", "Temperatura", "°C")]
    [InlineData("driveFrequency", "Elétrica", "Hz")]
    [InlineData("stockPumpDryMassSetpointKgH", "Bomba de massa", "kg/h")]
    [InlineData("stockPumpFlowSetpointM3h", "Bomba de massa", "m³/h")]
    [InlineData("stockPumpConsistencyUsedPct", "Bomba de massa", "%")]
    [InlineData("stockPumpSuggestedCalibrationFactor", "Bomba de massa", "fator")]
    [InlineData("stockPumpCurrent", "Bomba de massa", "A")]
    [InlineData("stockPumpAutomaticActive", "Bomba de massa", "0/1")]
    public void DescribesProcessUnits(
        string fieldName,
        string expectedCategory,
        string expectedUnit)
    {
        var definition = ProcessVariableCatalog.Describe(fieldName);

        Assert.Equal(expectedCategory, definition.Category);
        Assert.Equal(expectedUnit, definition.Unit);
    }
}
