using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Domain;
using PaperMachine.Historian.Infrastructure.Database;

namespace PaperMachine.Historian.Tests;

public sealed class SqliteUserBreakAnalysisFilterRepositoryTests
{
    [Fact]
    public async Task PreservesVariableOrderAndMaintainsOneDefaultPerUser()
    {
        var context = await TestContext.CreateAsync();
        try
        {
            var first = await context.Filters.CreateAsync(
                context.UserId,
                "Velocidades",
                ["speedG3", "speedG1", "speedG2"],
                true,
                context.Now,
                CancellationToken.None);
            var second = await context.Filters.CreateAsync(
                context.UserId,
                "Pressões",
                ["pressureG1", "pressureG2"],
                true,
                context.Now.AddMinutes(1),
                CancellationToken.None);

            Assert.Equal(UserBreakAnalysisFilterWriteStatus.Success, first.Status);
            Assert.Equal(UserBreakAnalysisFilterWriteStatus.Success, second.Status);
            var filters = await context.Filters.ListAsync(
                context.UserId,
                CancellationToken.None);

            Assert.Equal(2, filters.Count);
            Assert.Equal(second.Filter!.Id, filters[0].Id);
            Assert.True(filters[0].IsDefault);
            Assert.False(filters[1].IsDefault);
            Assert.Equal(2, filters[1].Revision);
            Assert.Equal(
                ["speedG3", "speedG1", "speedG2"],
                filters.Single(filter => filter.Id == first.Filter!.Id).Variables);
            Assert.Empty(await context.Filters.ListAsync(
                context.OtherUserId,
                CancellationToken.None));

            await context.DeleteUserAsync(context.UserId);
            Assert.Empty(await context.Filters.ListAsync(
                context.UserId,
                CancellationToken.None));
        }
        finally
        {
            context.Dispose();
        }
    }

    [Fact]
    public async Task EnforcesOwnershipAndOptimisticConcurrency()
    {
        var context = await TestContext.CreateAsync();
        try
        {
            var created = await context.Filters.CreateAsync(
                context.UserId,
                "Velocidade",
                ["speed"],
                false,
                context.Now,
                CancellationToken.None);
            var filter = Assert.IsType<UserBreakAnalysisFilter>(created.Filter);

            var foreignUpdate = await context.Filters.UpdateAsync(
                filter.Id,
                context.OtherUserId,
                "Inválido",
                ["other"],
                false,
                filter.Revision,
                context.Now.AddMinutes(1),
                CancellationToken.None);
            Assert.Equal(UserBreakAnalysisFilterWriteStatus.NotFound, foreignUpdate.Status);

            var updated = await context.Filters.UpdateAsync(
                filter.Id,
                context.UserId,
                "Velocidade das secagens",
                ["speedG1", "speedG2", "speedG3"],
                false,
                filter.Revision,
                context.Now.AddMinutes(1),
                CancellationToken.None);
            Assert.Equal(UserBreakAnalysisFilterWriteStatus.Success, updated.Status);
            Assert.Equal(2, updated.Filter!.Revision);

            var staleUpdate = await context.Filters.UpdateAsync(
                filter.Id,
                context.UserId,
                "Sobrescrita",
                ["stale"],
                false,
                filter.Revision,
                context.Now.AddMinutes(2),
                CancellationToken.None);
            Assert.Equal(
                UserBreakAnalysisFilterWriteStatus.RevisionConflict,
                staleUpdate.Status);

            Assert.Equal(
                UserBreakAnalysisFilterWriteStatus.NotFound,
                await context.Filters.DeleteAsync(
                    filter.Id,
                    context.OtherUserId,
                    updated.Filter.Revision,
                    CancellationToken.None));
            Assert.Equal(
                UserBreakAnalysisFilterWriteStatus.RevisionConflict,
                await context.Filters.DeleteAsync(
                    filter.Id,
                    context.UserId,
                    filter.Revision,
                    CancellationToken.None));
            Assert.Equal(
                UserBreakAnalysisFilterWriteStatus.Success,
                await context.Filters.DeleteAsync(
                    filter.Id,
                    context.UserId,
                    updated.Filter.Revision,
                    CancellationToken.None));
        }
        finally
        {
            context.Dispose();
        }
    }

    [Fact]
    public async Task EnforcesNameAndPerUserCountLimits()
    {
        var context = await TestContext.CreateAsync();
        try
        {
            for (var index = 1; index <= 25; index++)
            {
                var created = await context.Filters.CreateAsync(
                    context.UserId,
                    $"Filtro {index}",
                    [$"variable{index}"],
                    false,
                    context.Now.AddSeconds(index),
                    CancellationToken.None);
                Assert.Equal(UserBreakAnalysisFilterWriteStatus.Success, created.Status);
            }

            var overLimit = await context.Filters.CreateAsync(
                context.UserId,
                "Filtro 26",
                ["variable26"],
                false,
                context.Now.AddMinutes(1),
                CancellationToken.None);
            Assert.Equal(UserBreakAnalysisFilterWriteStatus.LimitReached, overLimit.Status);

            var otherUser = await context.Filters.CreateAsync(
                context.OtherUserId,
                "Filtro 1",
                ["otherVariable"],
                false,
                context.Now,
                CancellationToken.None);
            Assert.Equal(UserBreakAnalysisFilterWriteStatus.Success, otherUser.Status);

            var duplicate = await context.Filters.CreateAsync(
                context.OtherUserId,
                "filtro 1",
                ["otherVariable"],
                false,
                context.Now,
                CancellationToken.None);
            Assert.Equal(UserBreakAnalysisFilterWriteStatus.NameConflict, duplicate.Status);
        }
        finally
        {
            context.Dispose();
        }
    }

    private sealed class TestContext : IDisposable
    {
        private TestContext(
            string directory,
            string databasePath,
            SqliteUserBreakAnalysisFilterRepository filters,
            long userId,
            long otherUserId)
        {
            Directory = directory;
            DatabasePath = databasePath;
            Filters = filters;
            UserId = userId;
            OtherUserId = otherUserId;
        }

        public string Directory { get; }
        public string DatabasePath { get; }
        public SqliteUserBreakAnalysisFilterRepository Filters { get; }
        public long UserId { get; }
        public long OtherUserId { get; }
        public DateTimeOffset Now { get; } =
            new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

        public static async Task<TestContext> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "PaperMachine.Historian.Tests",
                Guid.NewGuid().ToString("N"));
            var options = new DatabaseOptions
            {
                FilePath = Path.Combine(directory, "historian.db")
            };
            await new SqliteHistorianRepository(options).InitializeAsync(
                CancellationToken.None);
            var users = new SqliteUserRepository(options);
            var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
            var userId = await users.CreateAsync(
                "andre",
                "André",
                "hash",
                HistorianRoles.Viewer,
                now,
                CancellationToken.None);
            var otherUserId = await users.CreateAsync(
                "maria",
                "Maria",
                "hash",
                HistorianRoles.Viewer,
                now,
                CancellationToken.None);
            return new(
                directory,
                options.FilePath,
                new SqliteUserBreakAnalysisFilterRepository(options),
                userId,
                otherUserId);
        }

        public async Task DeleteUserAsync(long userId)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                ForeignKeys = true
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ApplicationUsers WHERE Id = @Id;";
            command.Parameters.AddWithValue("@Id", userId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
