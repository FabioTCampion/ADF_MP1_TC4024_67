using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Infrastructure.Database;
using PaperMachine.Historian.Web;
using PdfSharp.Pdf.IO;

namespace PaperMachine.Historian.Tests;

public sealed class ProductionBreakReportServiceTests
{
    [Fact]
    public async Task GenerateAsync_CreatesAValidOperationalPdf()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.ReportTests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(testDirectory, "historian.db");

        try
        {
            var repository = new SqliteHistorianRepository(
                new DatabaseOptions { FilePath = databasePath });
            await repository.InitializeAsync(CancellationToken.None);
            var processor = new HistorianProcessor(
                new HistorianOptions
                {
                    TelemetrySampleIntervalSeconds = 1,
                    PaperBreakMinimumSpeedMpm = 5
                });
            var start = new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero);
            await PersistAsync(repository, processor, start, 120, true);
            await PersistAsync(repository, processor, start.AddMinutes(1), 118, false);
            await PersistAsync(repository, processor, start.AddMinutes(2), 115, false);
            await PersistAsync(repository, processor, start.AddMinutes(3), 110, true);
            await PersistAsync(repository, processor, start.AddMinutes(4), 125, true);

            var service = new ProductionBreakReportService(
                repository,
                new ReportingOptions(),
                TimeProvider.System,
                NullLogger<ProductionBreakReportService>.Instance);
            var report = await service.GenerateAsync(
                start,
                start.AddMinutes(5),
                productiveSpeedMpm: 10,
                requestedBy: "Validação local",
                CancellationToken.None);

            Assert.StartsWith(
                "%PDF-",
                System.Text.Encoding.ASCII.GetString(report.Content, 0, 5));
            Assert.Equal(
                "Relatorio-Producao-Quebras-2026-07-25-a-2026-07-25.pdf",
                report.FileName);
            using var document = PdfReader.Open(
                new MemoryStream(report.Content),
                PdfDocumentOpenMode.Import);
            Assert.True(document.PageCount >= 1);
            Assert.True(report.Content.Length > 20_000);

            var previewPath =
                Environment.GetEnvironmentVariable("PAPER_MACHINE_REPORT_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
                await File.WriteAllBytesAsync(previewPath, report.Content);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateAsync_RejectsPeriodLongerThanConfiguredLimit()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.ReportTests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(testDirectory, "historian.db");

        try
        {
            var repository = new SqliteHistorianRepository(
                new DatabaseOptions { FilePath = databasePath });
            await repository.InitializeAsync(CancellationToken.None);
            var service = new ProductionBreakReportService(
                repository,
                new ReportingOptions { MaximumRangeDays = 31 },
                TimeProvider.System,
                NullLogger<ProductionBreakReportService>.Instance);
            var start = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                service.GenerateAsync(
                    start,
                    start.AddDays(32),
                    productiveSpeedMpm: 10,
                    requestedBy: "Teste",
                    CancellationToken.None));

            Assert.Contains("31 dias", exception.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static Task PersistAsync(
        SqliteHistorianRepository repository,
        HistorianProcessor processor,
        DateTimeOffset capturedAtUtc,
        double speedMpm,
        bool paperPresent) =>
        repository.PersistCycleAsync(
            processor.Process(HistorianProcessorTests.CreateSnapshot(
                capturedAtUtc,
                $$"""
                {
                  "dryingSectionGroup3UpperMasterSpeedMPM": {{speedMpm.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                  "dryingSectionGroup3PaperPresence": {{paperPresent.ToString().ToLowerInvariant()}}
                }
                """,
                "{}",
                "{}")),
            CancellationToken.None);
}
