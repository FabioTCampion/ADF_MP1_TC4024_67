using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Infrastructure.Database;

public sealed class SqliteUserGraphLayoutRepository : IUserGraphLayoutRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteUserGraphLayoutRepository(DatabaseOptions options)
    {
        options.Validate();
        SQLitePCL.Batteries_V2.Init();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(options.FilePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task<UserGraphLayout?> GetAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindAsync(connection, userId, cancellationToken);
    }

    public async Task<UserGraphLayoutWriteResult> SaveAsync(
        long userId,
        IReadOnlyList<UserGraphPanel> charts,
        int expectedRevision,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var current = await FindAsync(connection, userId, cancellationToken);
            if (current is null
                    ? expectedRevision != 0
                    : current.Revision != expectedRevision)
            {
                return new(UserGraphLayoutWriteStatus.RevisionConflict, current);
            }

            var nextRevision = expectedRevision + 1;
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO UserGraphLayouts
                    (UserId, LayoutJson, Revision, UpdatedAtUtc)
                VALUES
                    (@UserId, @LayoutJson, @Revision, @UpdatedAtUtc)
                ON CONFLICT(UserId) DO UPDATE SET
                    LayoutJson = excluded.LayoutJson,
                    Revision = excluded.Revision,
                    UpdatedAtUtc = excluded.UpdatedAtUtc
                WHERE UserGraphLayouts.Revision = @ExpectedRevision;
                """;
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@LayoutJson", JsonSerializer.Serialize(charts));
            command.Parameters.AddWithValue("@Revision", nextRevision);
            command.Parameters.AddWithValue("@ExpectedRevision", expectedRevision);
            command.Parameters.AddWithValue("@UpdatedAtUtc", ToTimestamp(updatedAtUtc));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return new(
                    UserGraphLayoutWriteStatus.RevisionConflict,
                    await FindAsync(connection, userId, cancellationToken));
            }

            return new(
                UserGraphLayoutWriteStatus.Success,
                new UserGraphLayout(userId, charts, nextRevision, updatedAtUtc));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<UserGraphLayout?> FindAsync(
        SqliteConnection connection,
        long userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT UserId, LayoutJson, Revision, UpdatedAtUtc
            FROM UserGraphLayouts
            WHERE UserId = @UserId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@UserId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new UserGraphLayout(
            reader.GetInt64(0),
            JsonSerializer.Deserialize<UserGraphPanel[]>(reader.GetString(1)) ?? [],
            reader.GetInt32(2),
            ParseTimestamp(reader.GetString(3)));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string ToTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
