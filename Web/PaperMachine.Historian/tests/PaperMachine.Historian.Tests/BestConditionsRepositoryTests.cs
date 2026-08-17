using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Infrastructure.Database;

namespace PaperMachine.Historian.Tests;

public sealed class BestConditionsRepositoryTests
{
    [Fact]
    public async Task GroupsComparableProductionByProductAndGrammageIgnoringWidth()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "historian.db");
        var start = new DateTimeOffset(2026, 8, 17, 10, 0, 0, TimeSpan.Zero);
        try
        {
            var repository = new SqliteHistorianRepository(
                new DatabaseOptions { FilePath = databasePath });
            await repository.InitializeAsync(CancellationToken.None);
            await SeedAsync(databasePath, start);

            var qualities = await repository.GetComparableProductionQualitiesAsync(
                start.AddMinutes(-1),
                start.AddHours(3),
                CancellationToken.None);
            var quality = Assert.Single(qualities);
            Assert.Equal("MIOLO", quality.ProductCode);
            Assert.Equal(120, quality.GrammageGsm);
            Assert.Equal(2, quality.ProductionPeriodCount);

            var samples = await repository.GetBestConditionMinuteSamplesAsync(
                "miolo",
                120,
                start.AddMinutes(-1),
                start.AddHours(3),
                CancellationToken.None);
            Assert.Equal(2, samples.Count);
            Assert.Equal([1L, 2L], samples.Select(sample => sample.ProductionPeriodId));
            Assert.Equal(284, samples[0].NumericValues["headBoxMMH2O"]);
            Assert.Equal(1, samples[0].PaperPresentProbability);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task SeedAsync(string databasePath, DateTimeOffset start)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ExternalProductionRuns
                (Id, SourceSystem, ExternalRunId, ProductionOrderCode, IsProducing,
                 FirstObservedAtUtc, LastObservedAtUtc, QualityKey,
                 QualityProductCode, QualityGrammageGsm, ProductionWidthMm, IsMixedQuality)
            VALUES
                (1, 'PaperSystem', 'run-1', 'OP 1', 0, @Start, @End, 'MIOLO-120-1800',
                 'MIOLO', 120, 1800, 0),
                (2, 'PaperSystem', 'run-2', 'OP 2', 0, @Middle, @End, 'MIOLO-120-2500',
                 'MIOLO', 120, 2500, 0);

            INSERT INTO ProductionQualityPeriods
                (Id, SourceSystem, RunId, QualityKey, ProductCode, GrammageGsm,
                 ProductionWidthMm, IsMixedQuality, StartedAtUtc, EndedAtUtc)
            VALUES
                (1, 'PaperSystem', 1, 'MIOLO-120-1800', 'MIOLO', 120, 1800, 0, @Start, @Middle),
                (2, 'PaperSystem', 2, 'MIOLO-120-2500', 'MIOLO', 120, 2500, 0, @Middle, @End);

            INSERT INTO TelemetryMinuteAggregates
                (BucketUnixMs, SampleCount, MappingVersion, Quality,
                 dryingSectionGroup3UpperMasterSpeedMPM, MachineSpeedMaximum,
                 effectivePaperPresence, headBoxMMH2O)
            VALUES
                (@FirstBucket, 12, 'test-v1', 'Good', 401, 404, 1, 284),
                (@SecondBucket, 12, 'test-v1', 'Good', 405, 409, 1, 289);
            """;
        command.Parameters.AddWithValue("@Start", start.ToString("O"));
        command.Parameters.AddWithValue("@Middle", start.AddHours(1).ToString("O"));
        command.Parameters.AddWithValue("@End", start.AddHours(2).ToString("O"));
        command.Parameters.AddWithValue("@FirstBucket", start.AddMinutes(15).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@SecondBucket", start.AddHours(1).AddMinutes(15).ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync();
    }
}
