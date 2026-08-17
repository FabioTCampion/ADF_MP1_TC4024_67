using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;
using PaperMachine.Historian.Infrastructure.Database;
using PaperMachine.Historian.Web;

namespace PaperMachine.Historian.Tests;

public sealed class JumboWeightCaptureTests
{
    [Fact]
    public async Task QueuesVersionedWeightEventsInTheSameDatabase()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(testDirectory, "historian.db");

        try
        {
            var exportOptions = new WeightExportOptions
            {
                Enabled = true,
                MachineId = "MP1"
            };
            var repository = new SqliteHistorianRepository(
                new DatabaseOptions { FilePath = databasePath },
                exportOptions);
            await repository.InitializeAsync(CancellationToken.None);
            var runId = await InsertProductionRunAsync(databasePath);
            var capturedAt = new DateTimeOffset(2026, 8, 17, 14, 25, 30, TimeSpan.Zero);
            var fileTime = capturedAt.UtcDateTime.ToFileTimeUtc();
            var capture = new JumboWeightCapture(
                1_524,
                fileTime,
                capturedAt,
                capturedAt.AddSeconds(1),
                320,
                20,
                "test-v3",
                new JumboWeightProductionContext(
                    runId,
                    "PaperSystem",
                    "12345",
                    "98765",
                    null,
                    null,
                    null,
                    null,
                    capturedAt.AddSeconds(-10)));

            Assert.True(await repository.AddJumboWeightCaptureAsync(capture, CancellationToken.None));
            var created = await repository.GetNextWeightExportAsync(
                capturedAt.AddMinutes(1),
                CancellationToken.None);
            Assert.NotNull(created);
            Assert.Equal($"MP1:{fileTime}:1524", created.EventId);
            Assert.Equal("captured", created.EventType);
            Assert.Equal(1, created.Revision);
            using (var document = JsonDocument.Parse(created.PayloadJson))
            {
                Assert.Equal("12345", document.RootElement.GetProperty("productionMapId").GetString());
                Assert.Equal("98765", document.RootElement.GetProperty("productionOrder").GetString());
                Assert.Equal(320, document.RootElement.GetProperty("weightKg").GetDouble());
                Assert.EndsWith("Z", document.RootElement.GetProperty("capturedAtUtc").GetString());
            }

            await repository.MarkWeightExportDeliveredAsync(
                created.Id,
                created.EventId,
                capturedAt.AddMinutes(1),
                CancellationToken.None);
            var persisted = Assert.Single(await repository.GetJumboWeightCapturesAsync(
                null,
                null,
                10,
                CancellationToken.None));
            var correctedAt = capturedAt.AddMinutes(2);
            Assert.True(await repository.CorrectJumboWeightCaptureAsync(
                persisted.Id,
                321.5,
                "Peso conferido",
                "Supervisor",
                correctedAt,
                CancellationToken.None));
            var corrected = await repository.GetNextWeightExportAsync(
                correctedAt,
                CancellationToken.None);
            Assert.NotNull(corrected);
            Assert.Equal("corrected", corrected.EventType);
            Assert.Equal(2, corrected.Revision);

            await repository.MarkWeightExportDeliveredAsync(
                corrected.Id,
                corrected.EventId,
                correctedAt,
                CancellationToken.None);
            var deletedAt = capturedAt.AddMinutes(3);
            Assert.True(await repository.DeleteJumboWeightCaptureAsync(
                persisted.Id,
                "Apontamento anulado",
                "Supervisor",
                deletedAt,
                CancellationToken.None));
            var voided = await repository.GetNextWeightExportAsync(
                deletedAt,
                CancellationToken.None);
            Assert.NotNull(voided);
            Assert.Equal("voided", voided.EventType);
            Assert.Equal(3, voided.Revision);
            using var voidedDocument = JsonDocument.Parse(voided.PayloadJson);
            Assert.Equal(321.5, voidedDocument.RootElement.GetProperty("weightKg").GetDouble());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MapsWeightEndpointsIncludingDeleteRequestBody()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IHistorianRepository>(_ => null!);
        builder.Services.AddSingleton(TimeProvider.System);

        await using var app = builder.Build();
        app.MapJumboWeightCaptures();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        Assert.Contains(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/api/production/weights/{id:long}"
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    .Contains("DELETE") == true);
    }

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

            var correctedAt = capturedAt.AddMinutes(5);
            Assert.True(await repository.CorrectJumboWeightCaptureAsync(
                row.Id,
                5098.75,
                "Conferido no ticket da balança",
                "Supervisor Teste",
                correctedAt,
                CancellationToken.None));
            Assert.False(await repository.CorrectJumboWeightCaptureAsync(
                row.Id + 999,
                5000,
                "Registro inexistente",
                "Supervisor Teste",
                correctedAt,
                CancellationToken.None));

            var corrected = Assert.Single(await repository.GetJumboWeightCapturesAsync(
                capturedAt.AddMinutes(-1),
                capturedAt.AddMinutes(10),
                10,
                CancellationToken.None));
            Assert.Equal(5120.25, corrected.WeightKg);
            Assert.Equal(5098.75, corrected.CorrectedWeightKg);
            Assert.Equal("Conferido no ticket da balança", corrected.CorrectionReason);
            Assert.Equal("Supervisor Teste", corrected.CorrectedBy);
            Assert.Equal(correctedAt, corrected.CorrectedAtUtc);
            Assert.Equal(1, await CountWeightCorrectionsAsync(databasePath, row.Id));

            var deletedAt = capturedAt.AddMinutes(8);
            Assert.True(await repository.DeleteJumboWeightCaptureAsync(
                row.Id,
                "Apontamento duplicado confirmado",
                "Supervisor Teste",
                deletedAt,
                CancellationToken.None));
            Assert.False(await repository.DeleteJumboWeightCaptureAsync(
                row.Id,
                "Segunda exclusão não permitida",
                "Supervisor Teste",
                deletedAt.AddSeconds(1),
                CancellationToken.None));
            Assert.Empty(await repository.GetJumboWeightCapturesAsync(
                capturedAt.AddMinutes(-1),
                capturedAt.AddMinutes(10),
                10,
                CancellationToken.None));
            Assert.False(await repository.CorrectJumboWeightCaptureAsync(
                row.Id,
                5000,
                "Registro já excluído",
                "Supervisor Teste",
                deletedAt.AddSeconds(2),
                CancellationToken.None));

            var deletion = await ReadWeightDeletionAsync(databasePath, row.Id);
            Assert.Equal(5120.25, deletion.OriginalWeightKg);
            Assert.Equal(deletedAt, deletion.DeletedAtUtc);
            Assert.Equal("Supervisor Teste", deletion.DeletedBy);
            Assert.Equal("Apontamento duplicado confirmado", deletion.Reason);
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

    private static async Task<long> CountWeightCorrectionsAsync(string databasePath, long captureId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM JumboWeightCorrections
            WHERE JumboWeightCaptureId = @CaptureId;
            """;
        command.Parameters.AddWithValue("@CaptureId", captureId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<(double OriginalWeightKg, DateTimeOffset DeletedAtUtc, string DeletedBy, string Reason)>
        ReadWeightDeletionAsync(string databasePath, long captureId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT WeightKg, DeletedAtUtc, DeletedBy, DeletionReason
            FROM JumboWeightCaptures
            WHERE Id = @CaptureId;
            """;
        command.Parameters.AddWithValue("@CaptureId", captureId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetDouble(0),
            DateTimeOffset.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3));
    }
}
