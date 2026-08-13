using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;
using PaperMachine.Historian.Infrastructure.Database;
using PaperMachine.Historian.Web;

namespace PaperMachine.Historian.Tests;

public sealed class WeightReportServiceTests
{
    [Fact]
    public async Task MapsPdfAndExcelReportEndpointWithoutStartupErrors()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IProductionBreakReportService>(_ => null!);
        builder.Services.AddSingleton<IWeightReportService>(_ => null!);
        await using var app = builder.Build();

        app.MapReportEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Contains(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/api/reports/weights/{format}");
    }

    [Fact]
    public async Task GeneratesPdfAndFilteredExcelFromCapturedWeights()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.WeightReports",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(testDirectory, "historian.db");
        try
        {
            var repository = new SqliteHistorianRepository(
                new DatabaseOptions { FilePath = databasePath });
            await repository.InitializeAsync(CancellationToken.None);
            var capturedAt = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
            await repository.AddJumboWeightCaptureAsync(
                CreateCapture(42, capturedAt, 4_900),
                CancellationToken.None);
            await repository.AddJumboWeightCaptureAsync(
                CreateCapture(43, capturedAt.AddHours(1), 5_100),
                CancellationToken.None);
            var service = new WeightReportService(
                repository,
                new ReportingOptions { MachineName = "MP1" },
                TimeProvider.System,
                NullLogger<WeightReportService>.Instance);

            var pdf = await service.GeneratePdfAsync(
                capturedAt.AddMinutes(-1),
                capturedAt.AddHours(2),
                null,
                "Supervisor Teste",
                CancellationToken.None);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf.Content, 0, 5));
            Assert.EndsWith(".pdf", pdf.FileName, StringComparison.OrdinalIgnoreCase);

            var excel = await service.GenerateExcelAsync(
                capturedAt.AddMinutes(-1),
                capturedAt.AddHours(2),
                "42",
                "Supervisor Teste",
                CancellationToken.None);
            Assert.Equal("PK", Encoding.ASCII.GetString(excel.Content, 0, 2));
            Assert.EndsWith(".xlsx", excel.FileName, StringComparison.OrdinalIgnoreCase);
            using var archive = new ZipArchive(new MemoryStream(excel.Content), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries.Where(item => item.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                using var xmlStream = entry.Open();
                _ = XDocument.Load(xmlStream);
            }
            var worksheet = archive.GetEntry("xl/worksheets/sheet1.xml");
            Assert.NotNull(worksheet);
            using var reader = new StreamReader(worksheet!.Open());
            var xml = await reader.ReadToEndAsync();
            Assert.Contains(">42<", xml, StringComparison.Ordinal);
            Assert.DoesNotContain(">43<", xml, StringComparison.Ordinal);
            Assert.Contains("autoFilter", xml, StringComparison.Ordinal);
            var previewPath = Environment.GetEnvironmentVariable("PAPER_MACHINE_WEIGHT_EXCEL_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
                await File.WriteAllBytesAsync(previewPath, excel.Content);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static JumboWeightCapture CreateCapture(
        long eventCounter,
        DateTimeOffset capturedAt,
        double weightKg) =>
        new(
            eventCounter,
            capturedAt.UtcDateTime.ToFileTimeUtc(),
            capturedAt,
            capturedAt.AddSeconds(1),
            weightKg,
            20,
            "test-v1",
            null);
}
