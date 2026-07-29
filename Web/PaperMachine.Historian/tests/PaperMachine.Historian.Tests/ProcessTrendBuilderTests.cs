using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Tests;

public sealed class ProcessTrendBuilderTests
{
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
