namespace PaperMachine.Historian.Application;

public static class BestConditionsAnalyzer
{
    private static readonly TimeSpan SampleCadence = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaximumContinuousGap = TimeSpan.FromSeconds(90);

    public static IReadOnlyList<BestConditionRunAnalysis> Analyze(
        IReadOnlyList<BestConditionMinuteRow> samples,
        double minimumProductiveSpeedMpm = 10,
        double minimumRunMinutes = 30)
    {
        if (!double.IsFinite(minimumProductiveSpeedMpm) || minimumProductiveSpeedMpm is < 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(minimumProductiveSpeedMpm));
        if (!double.IsFinite(minimumRunMinutes) || minimumRunMinutes is < 1 or > 1_440)
            throw new ArgumentOutOfRangeException(nameof(minimumRunMinutes));

        var results = new List<BestConditionRunAnalysis>();
        foreach (var period in samples
                     .OrderBy(sample => sample.ProductionPeriodId)
                     .ThenBy(sample => sample.CapturedAtUtc)
                     .GroupBy(sample => sample.ProductionPeriodId))
        {
            var active = new List<BestConditionMinuteRow>();
            var sequence = 0;

            void AppendActive()
            {
                if (active.Count == 0)
                    return;
                var start = active[0].CapturedAtUtc;
                var end = active[^1].CapturedAtUtc + SampleCadence;
                var periodEnd = active[^1].ProductionPeriodEndUtc;
                if (periodEnd.HasValue && end > periodEnd.Value)
                    end = periodEnd.Value;
                var durationMinutes = (end - start).TotalMinutes;
                if (durationMinutes >= minimumRunMinutes)
                {
                    sequence++;
                    results.Add(Build(period.Key, sequence, active, start, end, durationMinutes));
                }
                active.Clear();
            }

            foreach (var sample in period)
            {
                var productive = string.Equals(sample.Quality, "Good", StringComparison.OrdinalIgnoreCase) &&
                    sample.SpeedMpm is { } speed && double.IsFinite(speed) &&
                    speed >= minimumProductiveSpeedMpm &&
                    sample.PaperPresentProbability is { } paper && paper >= 0.5 &&
                    sample.PaperBreakCount == 0;
                var hasGap = active.Count > 0 &&
                    sample.CapturedAtUtc - active[^1].CapturedAtUtc > MaximumContinuousGap;
                if (!productive || hasGap)
                {
                    AppendActive();
                    if (!productive)
                        continue;
                }
                active.Add(sample);
            }
            AppendActive();
        }

        return results
            .OrderByDescending(run => run.DurationMinutes)
            .ThenByDescending(run => run.AverageSpeedMpm)
            .ToArray();
    }

    private static BestConditionRunAnalysis Build(
        long periodId,
        int sequence,
        IReadOnlyList<BestConditionMinuteRow> samples,
        DateTimeOffset start,
        DateTimeOffset end,
        double durationMinutes)
    {
        var speeds = samples
            .Select(sample => sample.SpeedMpm)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();
        var averageSpeed = speeds.Average();
        var variance = speeds.Average(speed => Math.Pow(speed - averageSpeed, 2));
        var values = new Dictionary<string, double?>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, BestConditionValueRange>(StringComparer.Ordinal);

        foreach (var parameter in BestConditionsCatalog.Parameters)
        {
            var parameterValues = samples
                .Select(sample => BestConditionsCatalog.CalculateValue(parameter, sample.NumericValues))
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToArray();
            values[parameter.Key] = parameterValues.Length == 0
                ? null
                : parameterValues.Average();
            if (parameterValues.Length > 0)
            {
                ranges[parameter.Key] = new BestConditionValueRange(
                    parameterValues.Min(),
                    parameterValues.Max());
            }
        }

        var expectedSamples = Math.Max(1, (int)Math.Ceiling(durationMinutes));
        return new BestConditionRunAnalysis(
            $"{periodId}-{sequence}",
            samples[0].ProductionRunId,
            periodId,
            samples[0].ProductionOrderCode,
            samples[0].ProductCode,
            samples[0].GrammageGsm,
            start,
            end,
            durationMinutes,
            averageSpeed,
            samples.Max(sample => sample.MachineSpeedMaximumMpm ?? sample.SpeedMpm!.Value),
            Math.Sqrt(variance),
            Math.Min(100, samples.Count * 100d / expectedSamples),
            values,
            ranges);
    }
}

public sealed record BestConditionRunAnalysis(
    string Id,
    long ProductionRunId,
    long ProductionPeriodId,
    string? ProductionOrderCode,
    string ProductCode,
    double GrammageGsm,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    double DurationMinutes,
    double AverageSpeedMpm,
    double MaximumSpeedMpm,
    double SpeedVariationMpm,
    double CoveragePct,
    IReadOnlyDictionary<string, double?> Values,
    IReadOnlyDictionary<string, BestConditionValueRange> Ranges);

public sealed record BestConditionValueRange(double Minimum, double Maximum);
