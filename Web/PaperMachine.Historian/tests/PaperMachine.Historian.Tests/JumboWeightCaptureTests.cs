using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;
using PaperMachine.Historian.Infrastructure.Database;

namespace PaperMachine.Historian.Tests;

public sealed class JumboWeightCaptureTests
{
    [Fact]
    public void DetectsLatchedPlcCaptureAndConvertsFileTimeToUtc()
    {
        var capturedAt = new DateTimeOffset(2026, 8, 7, 14, 30, 0, TimeSpan.Zero);
        var fileTime = capturedAt.UtcDateTime.ToFileTimeUtc();
        var snapshot = HistorianProcessorTests.CreateSnapshot(
            capturedAt.AddSeconds(1),
            $$"""
            {
              "jumboCapturedWeightKg": 4876.5,
              "jumboCapturedAtFileTime": {{fileTime}},
              "jumboWeightCaptureEventCounter": 42,
              "jumboWeightCaptureStatus": 20
            }
            """,
            "{}",
            "{}");

        var detected = JumboWeightCaptureDetector.TryDetect(snapshot, out var capture);

        Assert.True(detected);
        Assert.NotNull(capture);
        Assert.Equal(42, capture.PlcEventCounter);
        Assert.Equal(fileTime, capture.CapturedAtFileTime);
        Assert.Equal(capturedAt, capture.CapturedAtUtc);
        Assert.Equal(4876.5, capture.WeightKg);
        Assert.Equal(20, capture.CaptureStatus);
        Assert.Null(capture.Production);
    }

    [Fact]
    public async Task PersistsEachPlcCaptureOnceWithProductionContext()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(testDirectory, "historian.db");

        try
        {
            var repository = new SqliteHistorianRepository(
                new DatabaseOptions { FilePath = databasePath });
            await repository.InitializeAsync(CancellationToken.None);
            var runId = await InsertProductionRunAsync(databasePath);
            var capturedAt = new DateTimeOffset(2026, 8, 7, 15, 0, 0, TimeSpan.Zero);
            var fileTime = capturedAt.UtcDateTime.ToFileTimeUtc();
            var capture = new JumboWeightCapture(
                7,
                fileTime,
                capturedAt,
                capturedAt.AddSeconds(1),
                5120.25,
                20,
                "test-v2",
                new JumboWeightProductionContext(
                    runId,
                    "PaperSystem",
                    "8609",
                    "1193",
                    "MIOLO|100",
                    "MIOLO",
                    100m,
                    1730m,
                    capturedAt.AddSeconds(-20)));

            Assert.True(await repository.AddJumboWeightCaptureAsync(
                capture,
                CancellationToken.None));
            Assert.False(await repository.AddJumboWeightCaptureAsync(
                capture,
                CancellationToken.None));

            var row = Assert.Single(await repository.GetJumboWeightCapturesAsync(
                capturedAt.AddMinutes(-1),
                capturedAt.AddMinutes(1),
                10,
                CancellationToken.None));
            Assert.Equal(5120.25, row.WeightKg);
            Assert.Equal(runId, row.ProductionRunId);
            Assert.Equal("1193", row.ProductionOrderCode);
            Assert.Equal("MIOLO", row.ProductCode);
            Assert.Equal(100, row.GrammageGsm);
            Assert.Equal(1730, row.ProductionWidthMm);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static async Task<long> InsertProductionRunAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ExternalProductionRuns (
                SourceSystem, ExternalRunId, ProductionOrderCode, MachineCode,
                IsProducing, FirstObservedAtUtc, LastObservedAtUtc,
                QualityKey, QualityProductCode, QualityGrammageGsm,
                ProductionWidthMm, IsMixedQuality)
            VALUES (
                'PaperSystem', '8609', '1193', 'MP-SC', 1,
                '2026-08-07T14:00:00.0000000+00:00',
                '2026-08-07T15:00:00.0000000+00:00',
                'MIOLO|100', 'MIOLO', 100, 1730, 0);
            SELECT last_insert_rowid();
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
