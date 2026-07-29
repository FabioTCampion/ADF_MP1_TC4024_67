using PaperMachine.Historian.Domain;
using PaperMachine.Historian.Infrastructure.Database;
using PaperMachine.Historian.Web;

namespace PaperMachine.Historian.Tests;

public sealed class GraphLayoutTests
{
    [Fact]
    public void ValidatesThreeChartsTitlesVariablesAndLimits()
    {
        Assert.Null(GraphLayoutEndpoints.Validate(ValidCharts()));
        Assert.NotNull(GraphLayoutEndpoints.Validate(ValidCharts()[..2]));
        Assert.NotNull(GraphLayoutEndpoints.Validate([
            new UserGraphPanel(" ", ["speed"]),
            .. ValidCharts()[1..]
        ]));
        Assert.NotNull(GraphLayoutEndpoints.Validate([
            new UserGraphPanel(
                "Muitas séries",
                Enumerable.Range(0, GraphLayoutEndpoints.MaximumVariablesPerChart + 1)
                    .Select(index => $"speed{index}")
                    .ToArray()),
            .. ValidCharts()[1..]
        ]));
        Assert.NotNull(GraphLayoutEndpoints.Validate([
            new UserGraphPanel("Duplicado", ["speed", "speed"]),
            .. ValidCharts()[1..]
        ]));
    }

    [Fact]
    public async Task PersistsPerUserLayoutAndRejectsStaleRevision()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.Tests",
            Guid.NewGuid().ToString("N"));
        var options = new DatabaseOptions
        {
            FilePath = Path.Combine(directory, "historian.db")
        };
        try
        {
            await new SqliteHistorianRepository(options).InitializeAsync(
                CancellationToken.None);
            var users = new SqliteUserRepository(options);
            var now = new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
            var userId = await users.CreateAsync(
                "graficos",
                "Operador de gráficos",
                "hash",
                HistorianRoles.Viewer,
                now,
                CancellationToken.None);
            var repository = new SqliteUserGraphLayoutRepository(options);

            var created = await repository.SaveAsync(
                userId,
                ValidCharts(),
                0,
                now,
                CancellationToken.None);
            Assert.Equal(UserGraphLayoutWriteStatus.Success, created.Status);
            Assert.Equal(1, created.Layout?.Revision);

            var loaded = await repository.GetAsync(userId, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal("Velocidade", loaded.Charts[0].Title);
            Assert.Equal(["speedG1", "speedG2"], loaded.Charts[0].Variables);

            var stale = await repository.SaveAsync(
                userId,
                ValidCharts(),
                0,
                now.AddMinutes(1),
                CancellationToken.None);
            Assert.Equal(UserGraphLayoutWriteStatus.RevisionConflict, stale.Status);
            Assert.Equal(1, stale.Layout?.Revision);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static UserGraphPanel[] ValidCharts() =>
    [
        new("Velocidade", ["speedG1", "speedG2"]),
        new("Torque", ["torqueG1"]),
        new("Pressão", ["pressureG1"])
    ];
}
