using PaperMachine.Historian.Application;
using PaperMachine.Historian.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace PaperMachine.Historian.Tests;

public sealed class SqliteHistorianRepositoryTests
{
    [Fact]
    public async Task InitializesNewDatabaseAndPersistsHistorianEvents()
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

            var processor = new HistorianProcessor(
                new HistorianOptions { StatusSnapshotIntervalSeconds = 10 });
            var firstAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
            await repository.PersistCycleAsync(
                processor.Process(HistorianProcessorTests.CreateSnapshot(
                    firstAt,
                    """{"speed":10.0,"mixingPumpFaultCode":0,"mixingPumpFaultTorque":0.0,"mixingPumpFaultEventCounter":0}""",
                    """{"start":false}""",
                    """{"mixingPumpFaultAlarm":false}""")),
                CancellationToken.None);
            await repository.PersistCycleAsync(
                processor.Process(HistorianProcessorTests.CreateSnapshot(
                    firstAt.AddSeconds(1),
                    """{"speed":11.0,"mixingPumpFaultCode":1,"mixingPumpFaultTorque":12.3,"mixingPumpFaultEventCounter":1}""",
                    """{"start":true}""",
                    """{"mixingPumpFaultAlarm":true}""")),
                CancellationToken.None);
            await repository.PersistCycleAsync(
                processor.Process(HistorianProcessorTests.CreateSnapshot(
                    firstAt.AddSeconds(2),
                    """{"speed":11.0,"mixingPumpFaultCode":1,"mixingPumpFaultTorque":12.3,"mixingPumpFaultEventCounter":1}""",
                    """{"start":false}""",
                    """{"mixingPumpFaultAlarm":false}""")),
                CancellationToken.None);

            Assert.True(File.Exists(databasePath));
            Assert.Single(await repository.GetStatusSnapshotsAsync(null, null, 10, CancellationToken.None));
            var trendSamples = await repository.GetStatusTrendSamplesAsync(
                firstAt,
                firstAt.AddMinutes(1),
                100,
                CancellationToken.None);
            Assert.Single(trendSamples);
            Assert.Equal(2, (await repository.GetCommandEventsAsync(null, null, 10, CancellationToken.None)).Count);

            var alarm = Assert.Single(
                await repository.GetAlarmEventsAsync(null, null, null, 10, CancellationToken.None));
            Assert.NotNull(alarm.ClearedAtUtc);
            Assert.Equal(1_000, alarm.DurationMilliseconds);
            Assert.Equal("Alto", alarm.Severity);
            Assert.Contains("Falha", alarm.DisplayName);
            Assert.Equal(1, alarm.DriveFaultCode);
            Assert.Equal("ocA", alarm.DriveFaultMnemonic);
            Assert.Equal(12.3, alarm.DriveFaultTorque);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }
}
