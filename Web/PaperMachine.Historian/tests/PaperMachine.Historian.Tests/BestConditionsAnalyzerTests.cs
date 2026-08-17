using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Tests;

public sealed class BestConditionsAnalyzerTests
{
    [Fact]
    public void BuildsContinuousProductiveRunsAndRealParameterRanges()
    {
        var start = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        var samples = new List<BestConditionMinuteRow>();
        for (var minute = 0; minute < 45; minute++)
        {
            samples.Add(Sample(
                start.AddMinutes(minute),
                speed: 400 + minute % 3,
                paper: 1,
                stockFlow: 42 + minute % 4));
        }
        samples.Add(Sample(start.AddMinutes(45), speed: 400, paper: 0, stockFlow: 43));
        for (var minute = 46; minute < 81; minute++)
        {
            samples.Add(Sample(
                start.AddMinutes(minute),
                speed: 415 + minute % 2,
                paper: 1,
                stockFlow: 46 + minute % 3));
        }

        var result = BestConditionsAnalyzer.Analyze(samples, 10, 30);

        Assert.Equal(2, result.Count);
        Assert.Equal(45, result[0].DurationMinutes);
        Assert.Equal("41-1", result[0].Id);
        Assert.InRange(result[0].AverageSpeedMpm, 400.9, 401.1);
        Assert.Equal(42, result[0].Ranges["stockFlow"].Minimum);
        Assert.Equal(45, result[0].Ranges["stockFlow"].Maximum);
        Assert.Equal(100, result[0].CoveragePct);
        Assert.Equal(35, result[1].DurationMinutes);
    }

    [Fact]
    public void SplitsRunWhenTelemetryHasAGapAndIgnoresShortSegments()
    {
        var start = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 20)
            .Select(minute => Sample(start.AddMinutes(minute), 400, 1, 42))
            .Concat(Enumerable.Range(25, 35)
                .Select(minute => Sample(start.AddMinutes(minute), 405, 1, 44)))
            .ToArray();

        var result = BestConditionsAnalyzer.Analyze(samples, 10, 30);

        var run = Assert.Single(result);
        Assert.Equal(start.AddMinutes(25), run.StartedAtUtc);
        Assert.Equal(35, run.DurationMinutes);
    }

    [Fact]
    public void PaperBreakEventAlwaysSplitsAProductiveSequence()
    {
        var start = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        var samples = Enumerable.Range(0, 71)
            .Select(minute => Sample(
                start.AddMinutes(minute),
                400,
                1,
                42,
                paperBreakCount: minute == 35 ? 1 : 0))
            .ToArray();

        var result = BestConditionsAnalyzer.Analyze(samples, 10, 30);

        Assert.Equal(2, result.Count);
        Assert.All(result, run => Assert.Equal(35, run.DurationMinutes));
    }

    [Fact]
    public void CatalogAddsEveryAnalysisSourceToHistorianTelemetry()
    {
        foreach (var field in BestConditionsCatalog.TelemetryFields)
            Assert.Contains(field, TelemetryCatalog.NumericFields);
    }

    private static BestConditionMinuteRow Sample(
        DateTimeOffset capturedAt,
        double speed,
        double paper,
        double stockFlow,
        int paperBreakCount = 0) =>
        new(
            41,
            7,
            "OP 1007",
            "MIOLO",
            120,
            capturedAt.Date,
            capturedAt.Date.AddDays(1),
            capturedAt,
            12,
            speed,
            speed + 2,
            paper,
            paperBreakCount,
            new Dictionary<string, double?>
            {
                ["stockPumpFlowM3h"] = stockFlow
            },
            "Good");
}
