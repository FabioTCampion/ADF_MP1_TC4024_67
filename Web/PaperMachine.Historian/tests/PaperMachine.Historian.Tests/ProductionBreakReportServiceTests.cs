using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
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
    public void AnalyzeBreaks_UsesOneMinuteAsInclusiveConfirmationBoundary()
    {
        var start = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            BreakEvent(1, start, durationMilliseconds: 59_999),
            BreakEvent(2, start.AddMinutes(2), durationMilliseconds: 60_000),
            BreakEvent(3, start.AddMinutes(4), durationMilliseconds: 120_000)
        };
        var productivity = new MachineProductivityAnalysis(
            TimeSpan.FromSeconds(5),
            [],
            [],
            CoveredMinutes: 60,
            ProductiveMinutes: 57,
            UnproductiveMinutes: 3,
            PaperPresentMinutes: 57,
            ProductivityPercent: 95,
            PaperPresencePercent: 95,
            ProductiveAverageSpeed: 300,
            GeneralAverageSpeed: 285,
            MaximumSpeed: 320,
            LongestProductiveRunMinutes: 57,
            HourlyProductivity: new double[24],
            HourlyUnproductiveMinutes: new double[24]);

        var analysis = ProductionBreakReportService.AnalyzeBreaks(
            events,
            productivity,
            start.AddHours(1),
            start.AddHours(1),
            TimeSpan.FromMinutes(1));

        Assert.Equal([2L, 3L], analysis.Breaks.Select(item => item.Event.Id));
        Assert.Equal(1.5, analysis.MttrMinutes);
        Assert.Equal(2, analysis.HourlyBreaks.Sum());
    }

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

            var excel = await service.GenerateExcelAsync(
                start,
                start.AddMinutes(5),
                productiveSpeedMpm: 10,
                requestedBy: "Validação local",
                CancellationToken.None);
            Assert.Equal((byte)'P', excel.Content[0]);
            Assert.Equal((byte)'K', excel.Content[1]);
            Assert.Equal(
                "Metricas-da-Maquina-2026-07-25-a-2026-07-25.xlsx",
                excel.FileName);
            Assert.True(excel.Content.Length > 7_000);
            using (var excelDocument = SpreadsheetDocument.Open(
                       new MemoryStream(excel.Content),
                       false))
            {
                var validationErrors = new OpenXmlValidator(
                    FileFormatVersions.Office2019).Validate(excelDocument).ToArray();
                Assert.Empty(validationErrors);
                var workbookPart = excelDocument.WorkbookPart
                    ?? throw new InvalidOperationException("A pasta de trabalho não foi encontrada.");
                var sheets = workbookPart.Workbook
                    .GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.Sheets>()
                    ?? throw new InvalidOperationException("As planilhas não foram encontradas.");
                var sheetNames = sheets
                    .Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()
                    .Select(sheet => sheet.Name?.Value ?? string.Empty)
                    .ToArray();
                Assert.Equal(
                    ["Resumo", "Desempenho horário", "Quebras", "Pareto"],
                    sheetNames);
            }

            var excelPreviewPath = Environment.GetEnvironmentVariable(
                "PAPER_MACHINE_METRICS_EXCEL_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(excelPreviewPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(excelPreviewPath)!);
                await File.WriteAllBytesAsync(excelPreviewPath, excel.Content);
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
                  "dryingSectionGroup3PaperPresence": {{paperPresent.ToString().ToLowerInvariant()}},
                  "stockPumpState": 1
                }
                """,
                "{}",
                "{}")),
            CancellationToken.None);

    private static PaperBreakEventRow BreakEvent(
        long id,
        DateTimeOffset startedAtUtc,
        long durationMilliseconds) =>
        new(
            id,
            startedAtUtc,
            startedAtUtc.AddMilliseconds(durationMilliseconds),
            durationMilliseconds,
            ActiveAtStartup: false,
            SpeedAtStartMpm: 300,
            SpeedAtEndMpm: 300,
            MappingVersion: "test",
            DiagnosticSampleCount: 0,
            AnalysisStatus: "Pendente",
            CauseCategory: null,
            CauseDescription: null,
            AnalysisNotes: null,
            AnalyzedBy: null,
            AnalyzedAtUtc: null);
}
