using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;
using PaperMachine.Historian.Infrastructure.Database;

namespace PaperMachine.Historian.Tests;

public sealed class SqliteUserRepositoryTests
{
    [Fact]
    public async Task CreatesFindsAndMarksAUserLogin()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(testDirectory, "historian.db");
        var options = new DatabaseOptions { FilePath = databasePath };

        try
        {
            await new SqliteHistorianRepository(options).InitializeAsync(CancellationToken.None);
            var repository = new SqliteUserRepository(options);
            var createdAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

            var id = await repository.CreateAsync(
                "admin",
                "Administrador",
                "password-hash",
                HistorianRoles.Administrator,
                createdAt,
                CancellationToken.None);

            Assert.Equal(1, await repository.CountAsync(CancellationToken.None));
            var created = Assert.IsType<ApplicationUser>(
                await repository.FindByUserNameAsync("ADMIN", CancellationToken.None));
            Assert.Equal(id, created.Id);
            Assert.Equal("Administrador", created.DisplayName);
            Assert.Null(created.LastLoginAtUtc);

            var loggedInAt = createdAt.AddMinutes(1);
            await repository.MarkLoginAsync(id, loggedInAt, CancellationToken.None);

            var updated = Assert.IsType<ApplicationUser>(
                await repository.FindByIdAsync(id, CancellationToken.None));
            Assert.Equal(loggedInAt, updated.LastLoginAtUtc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
                Directory.Delete(testDirectory, recursive: true);
        }
    }
}
