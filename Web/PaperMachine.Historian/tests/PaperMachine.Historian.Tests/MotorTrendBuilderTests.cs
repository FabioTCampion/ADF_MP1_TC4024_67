using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Tests;

public sealed class MotorTrendBuilderTests
{
    [Fact]
    public void DiscoversMotorPairsAndBuildsChronologicalValues()
    {
        var firstAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var rows = new[]
        {
            CreateRow(
                1,
                firstAt,
                """
                {
                  "dryingSectionGroup1UpperMasterSpeedMPM": 120.5,
                  "dryingSectionGroup1UpperMasterTorque": 44.2,
                  "stockPumpSpeed": 68.0,
                  "stockPumpTorque": 31.0,
                  "headBoxMMH2O": 350.0
                }
                """),
            CreateRow(
                2,
                firstAt.AddSeconds(10),
                """
                {
                  "dryingSectionGroup1UpperMasterSpeedMPM": 121.5,
                  "dryingSectionGroup1UpperMasterTorque": 45.2,
                  "stockPumpSpeed": 69.0,
                  "stockPumpTorque": 32.0,
                  "headBoxMMH2O": 351.0
                }
                """)
        };

        var trend = MotorTrendBuilder.Build(rows);

        Assert.Equal(2, trend.Motors.Count);
        var dryingMotor = Assert.Single(
            trend.Motors,
            motor => motor.Key == "dryingSectionGroup1UpperMaster");
        Assert.Equal("m/min", dryingMotor.SpeedUnit);
        Assert.Equal("PLC", dryingMotor.TorqueUnit);
        Assert.Equal(120.5, trend.Samples[0].Values[dryingMotor.Key].Speed);
        Assert.Equal(45.2, trend.Samples[1].Values[dryingMotor.Key].Torque);
    }

    private static StatusSnapshotRow CreateRow(
        long id,
        DateTimeOffset capturedAtUtc,
        string payloadJson) =>
        new(id, capturedAtUtc, payloadJson, "test-v1", "Good");
}
