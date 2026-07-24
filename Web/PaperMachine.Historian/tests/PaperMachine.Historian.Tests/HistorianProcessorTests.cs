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
            """{"speed":13.0,"running":true}""",
            """{"start":true,"setpoint":15.0}""",
            """{"driveFault":true}"""));

        var status = Assert.Single(cycle.StatusChanges);
        Assert.Equal("speed", status.FieldName);
        Assert.Equal("12.5", status.PreviousValueJson);
        Assert.Equal("13.0", status.CurrentValueJson);

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
