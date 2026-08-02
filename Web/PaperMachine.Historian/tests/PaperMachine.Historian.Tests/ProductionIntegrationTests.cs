using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Domain;
using PaperMachine.Historian.Infrastructure.Database;
using PaperMachine.Historian.Web;

namespace PaperMachine.Historian.Tests;

public sealed class ProductionIntegrationTests
{
    [Fact]
    public void ProductionIntegrationOnlyAcceptsLoopbackConnector()
    {
        var options = new ProductionIntegrationOptions
        {
            Enabled = true,
            ApiKeyFilePath = Path.GetFullPath("connector-token.txt")
        };
        options.Validate();

        options.BaseUrl = "http://10.8.0.4:5091";
        Assert.Throws<InvalidOperationException>(options.Validate);

        options.BaseUrl = "https://api.papersystem.com.br";
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void PaperSystemMappingPreservesContextAndBuildsProductionRecipe()
    {
        var response = new PaperSystemProductionSourceClient.PaperSystemResponse
        {
            IsProducing = true,
            ProductionMapId = 8609,
            ProductionOrder = 1193,
            Machine = "MP-SC",
            Items =
            [
                new PaperSystemProductionSourceClient.PaperSystemItem
                {
                    CustomerName = "P2A EMBALAGENS",
                    Order = 1984,
                    ProductCode = "MIOLO",
                    Format = 1130,
                    Grammage = 100,
                    PlannedQuantityKg = 7751
                },
                new PaperSystemProductionSourceClient.PaperSystemItem
                {
                    CustomerName = "GRUPO MERCO",
                    Order = 1954,
                    ProductCode = "MIOLO",
                    Format = 600,
                    Grammage = 100,
                    PlannedQuantityKg = 14405
                },
                new PaperSystemProductionSourceClient.PaperSystemItem
                {
                    ProductCode = "MIOLO",
                    Format = 70,
                    Grammage = 100
                },
                new PaperSystemProductionSourceClient.PaperSystemItem
                {
                    ProductCode = "MIOLO",
                    Format = 999,
                    Grammage = 100
                }
            ],
            Jumbos = [12380, 12381]
        };

        var observation = PaperSystemProductionSourceClient.Map(
            response,
            "{\"idmapaproducao\":8609}",
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("8609", observation.ExternalRunId);
        Assert.Equal("1193", observation.ProductionOrderCode);
        Assert.Equal("MIOLO-100-1800", observation.QualityKey);
        Assert.Equal(1800m, observation.ProductionWidthMm);
        Assert.False(observation.IsMixedQuality);
        Assert.Equal(4, observation.Items.Count);
        Assert.Equal("12381", observation.References[1].ReferenceValue);

        response = new PaperSystemProductionSourceClient.PaperSystemResponse
        {
            IsProducing = true,
            ProductionMapId = 8610,
            Items =
            [
                new PaperSystemProductionSourceClient.PaperSystemItem
                    { ProductCode = "MIOLO", Format = 1000, Grammage = 100 },
                new PaperSystemProductionSourceClient.PaperSystemItem
                    { ProductCode = "CAPA", Format = 700, Grammage = 120 }
            ]
        };
        observation = PaperSystemProductionSourceClient.Map(
            response,
            "{\"idmapaproducao\":8610}",
            new DateTimeOffset(2026, 8, 1, 12, 1, 0, TimeSpan.Zero));
        Assert.True(observation.IsMixedQuality);
        Assert.Equal("MIXED", observation.QualityKey);
        Assert.Null(observation.QualityProductCode);
    }

    [Fact]
    public async Task RepositoryStoresOnlyChangedSnapshotsAndKeepsFailureSeparateFromStop()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "historian.db");
        try
        {
            var options = new DatabaseOptions { FilePath = databasePath };
            await new SqliteHistorianRepository(options).InitializeAsync(CancellationToken.None);
            var repository = new SqliteProductionIntegrationRepository(options);
            var firstAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            var observation = Observation(firstAt, true, "MIOLO-100-1130", "payload-a");

            await repository.ApplyObservationAsync(observation, CancellationToken.None);
            await repository.ApplyObservationAsync(
                observation with { ObservedAtUtc = firstAt.AddMinutes(1) },
                CancellationToken.None);

            var state = await repository.GetStateAsync("PaperSystem", CancellationToken.None);
            Assert.Equal("Online", state.Status);
            Assert.True(state.CurrentRun!.IsProducing);
            Assert.Equal("MIOLO-100-1130", state.CurrentRun.QualityKey);
            Assert.Equal(1130m, state.CurrentRun.ProductionWidthMm);
            Assert.Equal(firstAt.AddMinutes(1), state.CurrentRun.LastObservedAtUtc);
            Assert.Equal(1, await ScalarAsync(databasePath,
                "SELECT COUNT(*) FROM ExternalProductionSnapshots;"));
            Assert.Equal(1, await ScalarAsync(databasePath,
                "SELECT COUNT(*) FROM ProductionQualityPeriods WHERE EndedAtUtc IS NULL;"));

            await repository.RecordFailureAsync(
                "PaperSystem",
                firstAt.AddMinutes(2),
                "timeout",
                CancellationToken.None);
            state = await repository.GetStateAsync("PaperSystem", CancellationToken.None);
            Assert.Equal("Faulted", state.Status);
            Assert.True(state.CurrentRun!.IsProducing);
            Assert.Null(state.CurrentRun.ClosedAtUtc);

            await repository.ApplyObservationAsync(
                Observation(firstAt.AddMinutes(3), false, "MIOLO-100-1130", "payload-stopped"),
                CancellationToken.None);
            state = await repository.GetStateAsync("PaperSystem", CancellationToken.None);
            Assert.False(state.CurrentRun!.IsProducing);
            Assert.NotNull(state.CurrentRun.ClosedAtUtc);
            Assert.Equal(0, await ScalarAsync(databasePath,
                "SELECT COUNT(*) FROM ProductionQualityPeriods WHERE EndedAtUtc IS NULL;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static ProductionSourceObservation Observation(
        DateTimeOffset observedAt,
        bool producing,
        string qualityKey,
        string rawPayload)
    {
        return new ProductionSourceObservation(
            "PaperSystem",
            "8609",
            "1193",
            "MP-SC",
            producing,
            null,
            observedAt,
            [new ExternalProductionItem(0, "Cliente", "1984", "MIOLO", 1130, 1300, 100, 7751, 0)],
            [new ExternalProductionReference(0, "Jumbo", "12380")],
            rawPayload,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload))),
            qualityKey,
            "MIOLO",
            100,
            1130,
            false);
    }

    private static async Task<long> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
