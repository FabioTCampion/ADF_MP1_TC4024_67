namespace PaperMachine.Historian.Application;

public static class MachineProductivityAnalyzer
{
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumContinuousGap = TimeSpan.FromMinutes(5);

    public static MachineProductivityAnalysis Analyze(
        IReadOnlyList<MachineProductivitySampleRow> samples,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        double productiveSpeedMpm)
    {
        if (periodStartUtc >= periodEndUtc)
            throw new ArgumentException("O início deve ser anterior ao fim do período.");
        if (!double.IsFinite(productiveSpeedMpm) || productiveSpeedMpm is < 0 or > 500)
            throw new ArgumentOutOfRangeException(
                nameof(productiveSpeedMpm),
                "A velocidade mínima produtiva deve estar entre 0 e 500 m/min.");

        var validSamples = samples
            .Where(IsValid)
            .OrderBy(sample => sample.CapturedAtUtc)
            .ToArray();
        var cadence = InferSampleInterval(validSamples);
        var intervals = BuildIntervals(
            validSamples,
            periodStartUtc,
            periodEndUtc,
            productiveSpeedMpm,
            cadence);

        var covered = TimeSpan.Zero;
        var productive = TimeSpan.Zero;
        var paperPresent = TimeSpan.Zero;
        var weightedProductiveSpeed = 0d;
        var weightedGeneralSpeed = 0d;
        var hourlyCovered = new double[24];
        var hourlyProductive = new double[24];

        foreach (var interval in intervals)
        {
            var duration = interval.EndUtc - interval.StartUtc;
            var durationMilliseconds = duration.TotalMilliseconds;
            covered += duration;
            weightedGeneralSpeed += interval.SpeedMpm * durationMilliseconds;
            if (interval.PaperPresent)
                paperPresent += duration;
            if (interval.Productive)
            {
                productive += duration;
                weightedProductiveSpeed += interval.SpeedMpm * durationMilliseconds;
            }

            SplitByLocalHour(interval, hourlyCovered, hourlyProductive);
        }

        var productiveRuns = BuildProductiveRuns(intervals);
        var unproductive = covered - productive;
        var hourlyProductivity = Enumerable.Range(0, 24)
            .Select(hour => hourlyCovered[hour] > 0
                ? hourlyProductive[hour] / hourlyCovered[hour] * 100
                : 0)
            .ToArray();
        var hourlyUnproductiveMinutes = Enumerable.Range(0, 24)
            .Select(hour => Math.Max(0, hourlyCovered[hour] - hourlyProductive[hour]) / 60_000)
            .ToArray();

        return new MachineProductivityAnalysis(
            cadence,
            intervals,
            productiveRuns,
            covered.TotalMinutes,
            productive.TotalMinutes,
            Math.Max(0, unproductive.TotalMinutes),
            paperPresent.TotalMinutes,
            covered > TimeSpan.Zero ? productive.TotalMilliseconds / covered.TotalMilliseconds * 100 : 0,
            covered > TimeSpan.Zero ? paperPresent.TotalMilliseconds / covered.TotalMilliseconds * 100 : 0,
            productive > TimeSpan.Zero ? weightedProductiveSpeed / productive.TotalMilliseconds : 0,
            covered > TimeSpan.Zero ? weightedGeneralSpeed / covered.TotalMilliseconds : 0,
            intervals.Count > 0 ? intervals.Max(interval => interval.SpeedMpm) : 0,
            productiveRuns.Count > 0 ? productiveRuns.Max(run => run.DurationMinutes) : null,
            hourlyProductivity,
            hourlyUnproductiveMinutes);
    }

    private static bool IsValid(MachineProductivitySampleRow sample) =>
        sample.SpeedMpm is { } speed &&
        double.IsFinite(speed) &&
        sample.PaperPresent.HasValue &&
        string.Equals(sample.Quality, "Good", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan InferSampleInterval(
        IReadOnlyList<MachineProductivitySampleRow> samples)
    {
        var occurrences = new Dictionary<long, int>();
        for (var index = 1; index < samples.Count; index++)
        {
            var interval = samples[index].CapturedAtUtc - samples[index - 1].CapturedAtUtc;
            if (interval <= TimeSpan.Zero || interval > MaximumContinuousGap)
                continue;

            var roundedMilliseconds =
                (long)Math.Round(interval.TotalMilliseconds / 1000, MidpointRounding.AwayFromZero) *
                1000;
            occurrences[roundedMilliseconds] =
                occurrences.GetValueOrDefault(roundedMilliseconds) + 1;
        }

        var selectedMilliseconds = (long)DefaultSampleInterval.TotalMilliseconds;
        var selectedCount = 0;
        foreach (var occurrence in occurrences)
        {
            if (occurrence.Value > selectedCount ||
                occurrence.Value == selectedCount && occurrence.Key < selectedMilliseconds)
            {
                selectedMilliseconds = occurrence.Key;
                selectedCount = occurrence.Value;
            }
        }

        return TimeSpan.FromMilliseconds(selectedMilliseconds);
    }

    private static IReadOnlyList<ProductivityIntervalAnalysis> BuildIntervals(
        IReadOnlyList<MachineProductivitySampleRow> samples,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        double productiveSpeedMpm,
        TimeSpan cadence)
    {
        var intervals = new List<ProductivityIntervalAnalysis>(samples.Count);
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            var nextAt = index + 1 < samples.Count
                ? samples[index + 1].CapturedAtUtc
                : sample.CapturedAtUtc + cadence;
            var start = sample.CapturedAtUtc < periodStartUtc
                ? periodStartUtc
                : sample.CapturedAtUtc;
            var end = Min(sample.CapturedAtUtc + cadence, nextAt, periodEndUtc);
            if (end <= start)
                continue;

            var speed = sample.SpeedMpm!.Value;
            var paper = sample.PaperPresent!.Value;
            intervals.Add(new ProductivityIntervalAnalysis(
                start,
                end,
                speed,
                paper,
                paper && speed >= productiveSpeedMpm));
        }

        return intervals;
    }

    private static IReadOnlyList<ProductiveRunAnalysis> BuildProductiveRuns(
        IReadOnlyList<ProductivityIntervalAnalysis> intervals)
    {
        var runs = new List<ProductiveRunAnalysis>();
        DateTimeOffset? activeStart = null;
        DateTimeOffset? activeEnd = null;

        void AppendActive()
        {
            if (activeStart.HasValue && activeEnd.HasValue)
                runs.Add(new ProductiveRunAnalysis(
                    activeStart.Value,
                    activeEnd.Value,
                    (activeEnd.Value - activeStart.Value).TotalMinutes));
            activeStart = null;
            activeEnd = null;
        }

        foreach (var interval in intervals)
        {
            if (!interval.Productive)
            {
                AppendActive();
                continue;
            }
            if (activeEnd.HasValue &&
                interval.StartUtc - activeEnd.Value > DefaultSampleInterval)
            {
                AppendActive();
            }

            activeStart ??= interval.StartUtc;
            activeEnd = interval.EndUtc;
        }

        AppendActive();
        return runs;
    }

    private static void SplitByLocalHour(
        ProductivityIntervalAnalysis interval,
        double[] hourlyCovered,
        double[] hourlyProductive)
    {
        var cursor = interval.StartUtc.ToLocalTime();
        var end = interval.EndUtc.ToLocalTime();
        while (cursor < end)
        {
            var nextHour = new DateTimeOffset(
                cursor.Year,
                cursor.Month,
                cursor.Day,
                cursor.Hour,
                0,
                0,
                cursor.Offset).AddHours(1);
            var segmentEnd = nextHour < end ? nextHour : end;
            var milliseconds = (segmentEnd - cursor).TotalMilliseconds;
            hourlyCovered[cursor.Hour] += milliseconds;
            if (interval.Productive)
                hourlyProductive[cursor.Hour] += milliseconds;
            cursor = segmentEnd;
        }
    }

    private static DateTimeOffset Min(
        DateTimeOffset first,
        DateTimeOffset second,
        DateTimeOffset third)
    {
        var result = first < second ? first : second;
        return result < third ? result : third;
    }
}

public sealed record ProductivityIntervalAnalysis(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double SpeedMpm,
    bool PaperPresent,
    bool Productive);

public sealed record ProductiveRunAnalysis(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double DurationMinutes);

public sealed record MachineProductivityAnalysis(
    TimeSpan SampleInterval,
    IReadOnlyList<ProductivityIntervalAnalysis> Intervals,
    IReadOnlyList<ProductiveRunAnalysis> ProductiveRuns,
    double CoveredMinutes,
    double ProductiveMinutes,
    double UnproductiveMinutes,
    double PaperPresentMinutes,
    double ProductivityPercent,
    double PaperPresencePercent,
    double ProductiveAverageSpeed,
    double GeneralAverageSpeed,
    double MaximumSpeed,
    double? LongestProductiveRunMinutes,
    IReadOnlyList<double> HourlyProductivity,
    IReadOnlyList<double> HourlyUnproductiveMinutes);
