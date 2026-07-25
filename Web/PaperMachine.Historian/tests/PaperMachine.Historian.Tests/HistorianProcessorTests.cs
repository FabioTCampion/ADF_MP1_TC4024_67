using System.Text.Json;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Tests;

public sealed class HistorianProcessorTests
{
    [Fact]
    public void InitialObservationCreatesBaselineAndReconcilesAllAlarms()
    {
        var processor = CreateProcessor();
        var capturedAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

        var cycle = processor.Process(CreateSnapshot(
            capturedAt,
            """{"speed":12.5,"running":true}""",
            """{"start":false,"setpoint":15.0}""",
            """{"driveFault":true,"safetyFault":false}"""));

        Assert.True(cycle.SaveStatusSnapshot);
        Assert.Empty(cycle.StatusChanges);
        Assert.Empty(cycle.CommandChanges);
        Assert.Collection(
            cycle.AlarmTransitions.OrderBy(item => item.AlarmName),
            item =>
            {
                Assert.Equal("driveFault", item.AlarmName);
                Assert.True(item.IsActive);
                Assert.True(item.InitialObservation);
            },
            item =>
            {
                Assert.Equal("safetyFault", item.AlarmName);
                Assert.False(item.IsActive);
                Assert.True(item.InitialObservation);
            });
    }

    [Fact]
    public void ChangedFieldsBecomeStatusCommandAndAlarmEvents()
    {
        var processor = CreateProcessor();
        var firstAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        processor.Process(CreateSnapshot(
            firstAt,
            """{"speed":12.5,"running":true}""",
            """{"start":false,"setpoint":15.0}""",
            """{"driveFault":false}"""));

        var cycle = processor.Process(CreateSnapshot(
            firstAt.AddSeconds(1),
            """{"speed":13.0,"running":false}""",
            """{"start":true,"setpoint":15.0}""",
            """{"driveFault":true}"""));

        var status = Assert.Single(cycle.StatusChanges);
        Assert.Equal("running", status.FieldName);
        Assert.Equal("true", status.PreviousValueJson);
        Assert.Equal("false", status.CurrentValueJson);
        Assert.DoesNotContain(cycle.StatusChanges, item => item.FieldName == "speed");

        var command = Assert.Single(cycle.CommandChanges);
        Assert.Equal("start", command.FieldName);
        Assert.Equal("false", command.PreviousValueJson);
        Assert.Equal("true", command.CurrentValueJson);

        var alarm = Assert.Single(cycle.AlarmTransitions);
        Assert.Equal("driveFault", alarm.AlarmName);
        Assert.True(alarm.IsActive);
        Assert.False(alarm.InitialObservation);
        Assert.False(cycle.SaveStatusSnapshot);
    }

    [Fact]
    public void StatusSnapshotUsesConfiguredInterval()
    {
        var processor = CreateProcessor();
        var firstAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var snapshot = CreateSnapshot(firstAt, """{"speed":1}""", """{"start":false}""", """{"fault":false}""");
        processor.Process(snapshot);

        var beforeInterval = processor.Process(snapshot with { CapturedAtUtc = firstAt.AddSeconds(9) });
        var atInterval = processor.Process(snapshot with { CapturedAtUtc = firstAt.AddSeconds(10) });

        Assert.False(beforeInterval.SaveStatusSnapshot);
        Assert.True(atInterval.SaveStatusSnapshot);
    }

    [Fact]
    public void TelemetryUsesDedicatedIntervalAndIgnoresAnalogStatusNoise()
    {
        var processor = new HistorianProcessor(new HistorianOptions
        {
            TelemetrySampleIntervalSeconds = 5,
            StatusSnapshotIntervalSeconds = 60
        });
        var firstAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var initial = CreateSnapshot(
            firstAt,
            """{"dryingSectionGroup3UpperMasterSpeedMPM":100.0,"running":true}""",
            """{"start":false}""",
            """{"fault":false}""");
        var first = processor.Process(initial);
        var beforeInterval = processor.Process(CreateSnapshot(
            firstAt.AddSeconds(4),
            """{"dryingSectionGroup3UpperMasterSpeedMPM":101.0,"running":true}""",
            """{"start":false}""",
            """{"fault":false}"""));
        var atInterval = processor.Process(CreateSnapshot(
            firstAt.AddSeconds(5),
            """{"dryingSectionGroup3UpperMasterSpeedMPM":102.0,"running":true}""",
            """{"start":false}""",
            """{"fault":false}"""));

        Assert.True(first.SaveTelemetrySample);
        Assert.False(beforeInterval.SaveTelemetrySample);
        Assert.True(atInterval.SaveTelemetrySample);
        Assert.Empty(beforeInterval.StatusChanges);
        Assert.Empty(atInterval.StatusChanges);
    }

    [Fact]
    public void PaperBreakIsCreatedAndClosedFromGroupThreeReference()
    {
        var processor = CreateProcessor();
        var firstAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var baseline = processor.Process(CreateSnapshot(
            firstAt,
            """{"dryingSectionGroup3UpperMasterSpeedMPM":120.0,"dryingSectionGroup3PaperPresence":true}""",
            """{"start":false}""",
            """{"fault":false}"""));
        var started = processor.Process(CreateSnapshot(
            firstAt.AddSeconds(1),
            """{"dryingSectionGroup3UpperMasterSpeedMPM":119.0,"dryingSectionGroup3PaperPresence":false}""",
            """{"start":false}""",
            """{"fault":false}"""));
        var ended = processor.Process(CreateSnapshot(
            firstAt.AddSeconds(8),
            """{"dryingSectionGroup3UpperMasterSpeedMPM":121.0,"dryingSectionGroup3PaperPresence":true}""",
            """{"start":false}""",
            """{"fault":false}"""));

        Assert.Single(baseline.PaperBreakTransitions);
        Assert.False(baseline.PaperBreakTransitions[0].IsActive);
        Assert.True(Assert.Single(started.PaperBreakTransitions).IsActive);
        var diagnostic = Assert.Single(started.PaperBreakDiagnostics);
        Assert.Equal(firstAt.AddSeconds(1), diagnostic.BreakAtUtc);
        Assert.Equal(2, diagnostic.Samples.Count);
        Assert.Equal(firstAt, diagnostic.Samples[0].CapturedAtUtc);
        Assert.Equal(firstAt.AddSeconds(1), diagnostic.Samples[1].CapturedAtUtc);
        Assert.False(Assert.Single(ended.PaperBreakTransitions).IsActive);
        Assert.Empty(ended.PaperBreakDiagnostics);
    }

    [Fact]
    public void PaperBreakDiagnosticKeepsOnlyConfiguredPreBreakWindow()
    {
        var options = new HistorianOptions
        {
            PaperBreakDiagnosticWindowSeconds = 30
        };
        var processor = new HistorianProcessor(options);
        var firstAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        processor.Process(CreateSnapshot(
            firstAt,
            """{"dryingSectionGroup3UpperMasterSpeedMPM":120.0,"dryingSectionGroup3PaperPresence":true}""",
            """{"start":false}""",
            """{"fault":false}"""));
        processor.Process(CreateSnapshot(
            firstAt.AddSeconds(20),
            """{"dryingSectionGroup3UpperMasterSpeedMPM":121.0,"dryingSectionGroup3PaperPresence":true}""",
            """{"start":false}""",
            """{"fault":false}"""));
        var started = processor.Process(CreateSnapshot(
            firstAt.AddSeconds(40),
            """{"dryingSectionGroup3UpperMasterSpeedMPM":119.0,"dryingSectionGroup3PaperPresence":false}""",
            """{"start":false}""",
            """{"fault":false}"""));

        var samples = Assert.Single(started.PaperBreakDiagnostics).Samples;
        Assert.Equal(2, samples.Count);
        Assert.DoesNotContain(samples, sample => sample.CapturedAtUtc == firstAt);
    }

    [Fact]
    public void DriveAlarmIncludesPortugueseCatalogCodeAndTorqueCapturedByPlc()
    {
        var processor = CreateProcessor();
        var firstAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        processor.Process(CreateSnapshot(
            firstAt,
            """
            {
              "dryingSectionGroup1UpperMasterFaultCode": 1,
              "dryingSectionGroup1UpperMasterFaultTorque": 0.0,
              "dryingSectionGroup1UpperMasterFaultEventCounter": 0
            }
            """,
            """{"start":false}""",
            """{"dryingSectionGroup1UpperMasterFaultAlarm":false}"""));

        var cycle = processor.Process(CreateSnapshot(
            firstAt.AddSeconds(1),
            """
            {
              "dryingSectionGroup1UpperMasterFaultCode": 1,
              "dryingSectionGroup1UpperMasterFaultTorque": 82.35,
              "dryingSectionGroup1UpperMasterFaultEventCounter": 7
            }
            """,
            """{"start":false}""",
            """{"dryingSectionGroup1UpperMasterFaultAlarm":true}"""));

        var alarm = Assert.Single(cycle.AlarmTransitions);
        Assert.Equal("Secagem", alarm.Definition.Area);
        Assert.Contains("Falha", alarm.Definition.DisplayName);
        Assert.NotNull(alarm.DriveFault);
        Assert.Equal("Delta C2000 Plus", alarm.DriveFault.Model);
        Assert.Equal((ushort)1, alarm.DriveFault.Code);
        Assert.Equal("0x0001", alarm.DriveFault.CodeHex);
        Assert.Equal("ocA", alarm.DriveFault.Mnemonic);
        Assert.Equal(82.35, alarm.DriveFault.TorqueAtTrip);
        Assert.Equal((uint)7, alarm.DriveFault.EventCounter);
    }

    private static HistorianProcessor CreateProcessor() =>
        new(new HistorianOptions { StatusSnapshotIntervalSeconds = 10 });

    internal static PaperMachineSnapshot CreateSnapshot(
        DateTimeOffset capturedAtUtc,
        string status,
        string commands,
        string alarms) =>
        new(
            capturedAtUtc,
            Parse(status),
            Parse(commands),
            Parse(alarms),
            "test-v1");

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
