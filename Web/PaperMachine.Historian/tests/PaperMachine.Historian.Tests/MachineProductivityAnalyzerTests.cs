using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Tests;

public sealed class MachineProductivityAnalyzerTests
{
    [Fact]
    public void Analyze_UsesPaperPresenceAndMinimumSpeedForProductivity()
    {
        var start = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            Sample(start, 20, true),
            Sample(start.AddSeconds(10), 20, true),
            Sample(start.AddSeconds(20), 20, false),
            Sample(start.AddSeconds(30), 20, false)
        };

        var analysis = MachineProductivityAnalyzer.Analyze(
            samples,
            start,
            start.AddSeconds(40),
            productiveSpeedMpm: 10);

        Assert.Equal(TimeSpan.FromSeconds(10), analysis.SampleInterval);
        Assert.Equal(4, analysis.Intervals.Count);
        Assert.Equal(40d / 60, analysis.CoveredMinutes, 6);
        Assert.Equal(20d / 60, analysis.ProductiveMinutes, 6);
        Assert.Equal(20d / 60, analysis.UnproductiveMinutes, 6);
        Assert.Equal(50, analysis.ProductivityPercent, 6);
        Assert.Equal(50, analysis.PaperPresencePercent, 6);
        Assert.Equal(20, analysis.ProductiveAverageSpeed, 6);
        Assert.Equal(20d / 60, analysis.LongestProductiveRunMinutes!.Value, 6);
    }

    [Fact]
    public void Analyze_IgnoresBadOrIncompleteSamples()
    {
        var start = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            Sample(start, 30, true),
            new MachineProductivitySampleRow(start.AddSeconds(10), 40, true, "Bad"),
            new MachineProductivitySampleRow(start.AddSeconds(20), null, true, "Good"),
            new MachineProductivitySampleRow(start.AddSeconds(30), 40, null, "Good")
        };

        var analysis = MachineProductivityAnalyzer.Analyze(
            samples,
            start,
            start.AddMinutes(1),
            productiveSpeedMpm: 10);

        Assert.Single(analysis.Intervals);
        Assert.Equal(10d / 60, analysis.CoveredMinutes, 6);
        Assert.Equal(100, analysis.ProductivityPercent, 6);
        Assert.Equal(30, analysis.MaximumSpeed, 6);
    }

    [Fact]
    public void Analyze_UsesMinuteCadenceForAggregatedPeriods()
    {
        var start = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 5)
            .Select(index => Sample(start.AddMinutes(index), 50, true))
            .ToArray();

        var analysis = MachineProductivityAnalyzer.Analyze(
            samples,
            start,
            start.AddMinutes(5),
            productiveSpeedMpm: 10);

        Assert.Equal(TimeSpan.FromMinutes(1), analysis.SampleInterval);
        Assert.Equal(5, analysis.ProductiveMinutes, 6);
        Assert.Equal(100, analysis.ProductivityPercent, 6);
    }

    private static MachineProductivitySampleRow Sample(
        DateTimeOffset capturedAtUtc,
        double speedMpm,
        bool paperPresent) =>
        new(capturedAtUtc, speedMpm, paperPresent, "Good");
}
