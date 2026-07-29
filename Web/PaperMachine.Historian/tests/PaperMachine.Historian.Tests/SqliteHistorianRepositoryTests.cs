using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;
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
                    """{"speed":10.0,"dryingSectionGroup3UpperMasterSpeedMPM":336.7,"dryingSectionGroup3PaperPresence":true,"stockPumpState":1,"mixingPumpFaultCode":0,"mixingPumpFaultTorque":0.0,"mixingPumpFaultEventCounter":0}""",
                    """{"start":false}""",
                    """{"mixingPumpFaultAlarm":false}""")),
                CancellationToken.None);
            await repository.AddCommandEventAsync(
                new FieldChange(
                    "pulse",
                    "false",
                    "true",
                    firstAt.AddMilliseconds(2_250)),
                "test-v1",
                CancellationToken.None);
            await repository.PersistCycleAsync(
                processor.Process(HistorianProcessorTests.CreateSnapshot(
                    firstAt.AddSeconds(1),
                    """{"speed":11.0,"dryingSectionGroup3UpperMasterSpeedMPM":335.0,"dryingSectionGroup3PaperPresence":false,"stockPumpState":1,"headBoxMMH2O":245.5,"headboxLipsPosition_mm":8.2,"mixingPumpFaultCode":1,"mixingPumpFaultTorque":12.3,"mixingPumpFaultEventCounter":1}""",
                    """{"start":true}""",
                    """{"mixingPumpFaultAlarm":true}""")),
                CancellationToken.None);
            await repository.PersistCycleAsync(
                processor.Process(HistorianProcessorTests.CreateSnapshot(
                    firstAt.AddSeconds(2),
                    """{"speed":11.0,"dryingSectionGroup3UpperMasterSpeedMPM":334.0,"dryingSectionGroup3PaperPresence":true,"stockPumpState":1,"headBoxMMH2O":246.0,"headboxLipsPosition_mm":8.2,"mixingPumpFaultCode":1,"mixingPumpFaultTorque":12.3,"mixingPumpFaultEventCounter":1}""",
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
            var optimizedTrend = await repository.GetTelemetryTrendSamplesAsync(
                firstAt,
                firstAt.AddMinutes(1),
                100,
                CancellationToken.None);
            Assert.Single(optimizedTrend);
            Assert.Equal(
                336.7,
                optimizedTrend[0].NumericValues[
                    TelemetryCatalog.MachineSpeedField]);
            var productivitySample = Assert.Single(
                await repository.GetMachineProductivitySamplesAsync(
                    firstAt,
                    firstAt.AddMinutes(1),
                    CancellationToken.None));
            Assert.Equal(336.7, productivitySample.SpeedMpm);
            Assert.True(productivitySample.PaperPresent);
            var commandEvents =
                await repository.GetCommandEventsAsync(null, null, 10, CancellationToken.None);
            Assert.Equal(3, commandEvents.Count);
            Assert.Contains(
                commandEvents,
                item => item.CommandName == "pulse" && item.Origin == "AdsOnChange");
            var commandPage = await repository.SearchCommandEventsAsync(
                firstAt.AddMinutes(-1),
                firstAt.AddMinutes(1),
                "pulse",
                0,
                1,
                CancellationToken.None);
            Assert.Equal(1, commandPage.Total);
            Assert.Single(commandPage.Items);
            Assert.False(commandPage.HasMore);
            var firstCommandPage = await repository.SearchCommandEventsAsync(
                firstAt.AddMinutes(-1),
                firstAt.AddMinutes(1),
                null,
                0,
                1,
                CancellationToken.None);
            Assert.Equal(3, firstCommandPage.Total);
            Assert.True(firstCommandPage.HasMore);

            var statusPage = await repository.SearchStatusChangesAsync(
                firstAt.AddMinutes(-1),
                firstAt.AddMinutes(1),
                "dryingSectionGroup3PaperPresence",
                0,
                10,
                CancellationToken.None);
            Assert.True(statusPage.Total >= 1);
            Assert.All(
                statusPage.Items,
                item => Assert.Contains(
                    "dryingSectionGroup3PaperPresence",
                    item.FieldName,
                    StringComparison.OrdinalIgnoreCase));

            var alarm = Assert.Single(
                await repository.GetAlarmEventsAsync(null, null, null, 10, CancellationToken.None));
            Assert.NotNull(alarm.ClearedAtUtc);
            Assert.Equal(1_000, alarm.DurationMilliseconds);
            Assert.Equal("Alto", alarm.Severity);
            Assert.Contains("Falha", alarm.DisplayName);
            Assert.Equal(1, alarm.DriveFaultCode);
            Assert.Equal("ocA", alarm.DriveFaultMnemonic);
            Assert.Equal(12.3, alarm.DriveFaultTorque);
            var alarmPage = await repository.SearchAlarmEventsAsync(
                firstAt.AddMinutes(-1),
                firstAt.AddMinutes(1),
                null,
                "ocA",
                0,
                10,
                CancellationToken.None);
            Assert.Equal(1, alarmPage.Total);
            Assert.Single(alarmPage.Items);

            var paperBreak = Assert.Single(
                await repository.GetPaperBreakEventsAsync(
                    null,
                    null,
                    minimumDurationMilliseconds: null,
                    10,
                    CancellationToken.None));
            Assert.Equal(2, paperBreak.DiagnosticSampleCount);
            Assert.Equal("Pendente", paperBreak.AnalysisStatus);
            Assert.Empty(await repository.GetPaperBreakEventsAsync(
                null,
                null,
                minimumDurationMilliseconds: 60_000,
                10,
                CancellationToken.None));
            var breakPage = await repository.SearchPaperBreakEventsAsync(
                firstAt.AddMinutes(-1),
                firstAt.AddMinutes(1),
                "Pendente",
                null,
                "Pendente",
                0,
                10,
                CancellationToken.None);
            Assert.Equal(1, breakPage.Total);
            Assert.Single(breakPage.Items);
            var diagnostic = await repository.GetPaperBreakDiagnosticAsync(
                paperBreak.Id,
                CancellationToken.None);
            Assert.NotNull(diagnostic);
            Assert.Equal(2, diagnostic.Samples.Count);
            Assert.Contains(
                diagnostic.Summary,
                item => item.FieldName == "headBoxMMH2O" && item.Unit == "mmH₂O");
            Assert.Contains(
                diagnostic.Evidence,
                item => item.Kind == "Comando" && item.Name == "start");
            Assert.Contains(
                diagnostic.Evidence,
                item => item.Kind == "Alarme" && item.Name == "mixingPumpFaultAlarm");
            Assert.True(await repository.UpdatePaperBreakAnalysisAsync(
                paperBreak.Id,
                new PaperBreakAnalysisUpdate(
                    "Concluída",
                    "Processo",
                    "Oscilação observada.",
                    "Validado no teste."),
                "tester",
                firstAt.AddMinutes(1),
                CancellationToken.None));
            var analyzed = await repository.GetPaperBreakDiagnosticAsync(
                paperBreak.Id,
                CancellationToken.None);
            Assert.NotNull(analyzed);
            Assert.Equal("Concluída", analyzed.Event.AnalysisStatus);
            Assert.Equal("tester", analyzed.Event.AnalyzedBy);

            var storage = await repository.GetStorageStatusAsync(CancellationToken.None);
            Assert.Equal(1, storage.TelemetrySampleCount);
            Assert.True(storage.DatabaseBytes > 0);
            Assert.True(storage.FreeDiskBytes > 0);
            Assert.True(storage.TotalDiskBytes >= storage.FreeDiskBytes);

            var maintenanceAt = firstAt.AddHours(1);
            var maintenance = await repository.RunMaintenanceAsync(
                maintenanceAt,
                new HistorianOptions
                {
                    RetentionEnabled = false,
                    MaintenanceBatchSize = 100,
                },
                CancellationToken.None);
            Assert.Equal(maintenanceAt, maintenance.CompletedAtUtc);

            var storageAfterMaintenance =
                await repository.GetStorageStatusAsync(CancellationToken.None);
            Assert.Equal(maintenanceAt, storageAfterMaintenance.LastMaintenanceAtUtc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }
}
