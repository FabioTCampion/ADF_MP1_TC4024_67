using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Domain;
using PaperMachine.Historian.Infrastructure.Database;
using PaperMachine.Historian.Web;

namespace PaperMachine.Historian.Tests;

public sealed class BreakAnalysisFilterEndpointsTests
{
    [Fact]
    public void ValidatesNamesVariablesFormatCountAndUniqueness()
    {
        Assert.Null(BreakAnalysisFilterEndpoints.Validate(
            "Velocidade G1/G2/G3",
            ["speedG1", "speedG2", "speedG3"]));
        Assert.NotNull(BreakAnalysisFilterEndpoints.Validate(" ", ["speedG1"]));
        Assert.NotNull(BreakAnalysisFilterEndpoints.Validate(
            new string('x', BreakAnalysisFilterEndpoints.MaximumNameLength + 1),
            ["speedG1"]));
        Assert.NotNull(BreakAnalysisFilterEndpoints.Validate("Filtro", []));
        Assert.NotNull(BreakAnalysisFilterEndpoints.Validate(
            "Filtro",
            Enumerable.Range(
                0,
                BreakAnalysisFilterEndpoints.MaximumVariablesPerFilter + 1)
                .Select(index => $"field{index}")
                .ToArray()));
        Assert.NotNull(BreakAnalysisFilterEndpoints.Validate(
            "Filtro",
            ["speedG1", "speedG1"]));
        Assert.NotNull(BreakAnalysisFilterEndpoints.Validate(
            "Filtro",
            ["estrutura.campo"]));
        Assert.NotNull(BreakAnalysisFilterEndpoints.Validate(
            "Filtro",
            [new string('x', BreakAnalysisFilterEndpoints.MaximumVariableNameLength + 1)]));
    }

    [Fact]
    public async Task ApiUsesClaimOwnershipAndReportsRevisionConflict()
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
            var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
            var andreId = await users.CreateAsync(
                "andre",
                "André",
                "hash",
                HistorianRoles.Viewer,
                now,
                CancellationToken.None);
            var mariaId = await users.CreateAsync(
                "maria",
                "Maria",
                "hash",
                HistorianRoles.Viewer,
                now,
                CancellationToken.None);
            var repository = new SqliteUserBreakAnalysisFilterRepository(options);
            var clock = new FixedTimeProvider(now);

            var createdResult = await BreakAnalysisFilterEndpoints.CreateAsync(
                new(
                    "Velocidade G1/G2/G3",
                    ["speedG1", "speedG2", "speedG3"],
                    true),
                Principal(andreId),
                repository,
                clock,
                CancellationToken.None);
            Assert.Equal(
                StatusCodes.Status201Created,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(createdResult).StatusCode);
            var created = Assert.IsType<UserBreakAnalysisFilter>(
                Assert.IsAssignableFrom<IValueHttpResult>(createdResult).Value);

            var foreignUpdate = await BreakAnalysisFilterEndpoints.UpdateAsync(
                created.Id,
                new(created.Name, ["other"], false, created.Revision),
                Principal(mariaId),
                repository,
                clock,
                CancellationToken.None);
            Assert.Equal(
                StatusCodes.Status404NotFound,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(foreignUpdate).StatusCode);

            var updatedResult = await BreakAnalysisFilterEndpoints.UpdateAsync(
                created.Id,
                new(created.Name, ["speedG3", "speedG2"], true, created.Revision),
                Principal(andreId),
                repository,
                clock,
                CancellationToken.None);
            Assert.Equal(
                StatusCodes.Status200OK,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(updatedResult).StatusCode);

            var staleUpdate = await BreakAnalysisFilterEndpoints.UpdateAsync(
                created.Id,
                new(created.Name, ["speedG1"], true, created.Revision),
                Principal(andreId),
                repository,
                clock,
                CancellationToken.None);
            Assert.Equal(
                StatusCodes.Status409Conflict,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(staleUpdate).StatusCode);

            var missingRevision = await BreakAnalysisFilterEndpoints.DeleteAsync(
                created.Id,
                null,
                Principal(andreId),
                repository,
                CancellationToken.None);
            Assert.Equal(
                StatusCodes.Status400BadRequest,
                Assert.IsAssignableFrom<IStatusCodeHttpResult>(missingRevision).StatusCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static ClaimsPrincipal Principal(long userId) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "Test"));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
