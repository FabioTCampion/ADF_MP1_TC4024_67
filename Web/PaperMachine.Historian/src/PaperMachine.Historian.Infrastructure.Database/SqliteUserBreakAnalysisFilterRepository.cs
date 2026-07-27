using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Infrastructure.Database;

public sealed class SqliteUserBreakAnalysisFilterRepository
    : IUserBreakAnalysisFilterRepository
{
    private const int MaximumFiltersPerUser = 25;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteUserBreakAnalysisFilterRepository(DatabaseOptions options)
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

    public async Task<IReadOnlyList<UserBreakAnalysisFilter>> ListAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, UserId, Name, VariablesJson, IsDefault, Revision,
                   CreatedAtUtc, UpdatedAtUtc
            FROM UserBreakAnalysisFilters
            WHERE UserId = @UserId
            ORDER BY IsDefault DESC, Name COLLATE NOCASE, Id;
            """;
        command.Parameters.AddWithValue("@UserId", userId);

        var filters = new List<UserBreakAnalysisFilter>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            filters.Add(ReadFilter(reader));
        return filters;
    }

    public async Task<UserBreakAnalysisFilterWriteResult> CreateAsync(
        long userId,
        string name,
        IReadOnlyList<string> variables,
        bool isDefault,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            if (await CountAsync(connection, transaction, userId, cancellationToken) >=
                MaximumFiltersPerUser)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(UserBreakAnalysisFilterWriteStatus.LimitReached);
            }
            if (await NameExistsAsync(
                    connection,
                    transaction,
                    userId,
                    name,
                    exceptId: null,
                    cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(UserBreakAnalysisFilterWriteStatus.NameConflict);
            }

            if (isDefault)
                await ClearDefaultAsync(
                    connection,
                    transaction,
                    userId,
                    exceptId: null,
                    createdAtUtc,
                    cancellationToken);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO UserBreakAnalysisFilters
                    (UserId, Name, VariablesJson, IsDefault, Revision,
                     CreatedAtUtc, UpdatedAtUtc)
                VALUES
                    (@UserId, @Name, @VariablesJson, @IsDefault, 1,
                     @CreatedAtUtc, @UpdatedAtUtc);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@Name", name.Trim());
            command.Parameters.AddWithValue("@VariablesJson", SerializeVariables(variables));
            command.Parameters.AddWithValue("@IsDefault", isDefault ? 1 : 0);
            command.Parameters.AddWithValue("@CreatedAtUtc", ToTimestamp(createdAtUtc));
            command.Parameters.AddWithValue("@UpdatedAtUtc", ToTimestamp(createdAtUtc));
            var id = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            var filter = await FindAsync(
                connection,
                transaction,
                id,
                userId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(UserBreakAnalysisFilterWriteStatus.Success, filter);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return new(UserBreakAnalysisFilterWriteStatus.NameConflict);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<UserBreakAnalysisFilterWriteResult> UpdateAsync(
        long id,
        long userId,
        string name,
        IReadOnlyList<string> variables,
        bool isDefault,
        int expectedRevision,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var current = await FindAsync(
                connection,
                transaction,
                id,
                userId,
                cancellationToken);
            if (current is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(UserBreakAnalysisFilterWriteStatus.NotFound);
            }
            if (current.Revision != expectedRevision)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(UserBreakAnalysisFilterWriteStatus.RevisionConflict, current);
            }
            if (await NameExistsAsync(
                    connection,
                    transaction,
                    userId,
                    name,
                    id,
                    cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(UserBreakAnalysisFilterWriteStatus.NameConflict);
            }

            if (isDefault)
                await ClearDefaultAsync(
                    connection,
                    transaction,
                    userId,
                    id,
                    updatedAtUtc,
                    cancellationToken);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE UserBreakAnalysisFilters
                SET Name = @Name,
                    VariablesJson = @VariablesJson,
                    IsDefault = @IsDefault,
                    Revision = Revision + 1,
                    UpdatedAtUtc = @UpdatedAtUtc
                WHERE Id = @Id AND UserId = @UserId AND Revision = @ExpectedRevision;
                """;
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@Name", name.Trim());
            command.Parameters.AddWithValue("@VariablesJson", SerializeVariables(variables));
            command.Parameters.AddWithValue("@IsDefault", isDefault ? 1 : 0);
            command.Parameters.AddWithValue("@ExpectedRevision", expectedRevision);
            command.Parameters.AddWithValue("@UpdatedAtUtc", ToTimestamp(updatedAtUtc));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(UserBreakAnalysisFilterWriteStatus.RevisionConflict);
            }

            var updated = await FindAsync(
                connection,
                transaction,
                id,
                userId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(UserBreakAnalysisFilterWriteStatus.Success, updated);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return new(UserBreakAnalysisFilterWriteStatus.NameConflict);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<UserBreakAnalysisFilterWriteStatus> DeleteAsync(
        long id,
        long userId,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM UserBreakAnalysisFilters
                WHERE Id = @Id AND UserId = @UserId AND Revision = @ExpectedRevision;
                """;
            command.Parameters.AddWithValue("@Id", id);
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@ExpectedRevision", expectedRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
                return UserBreakAnalysisFilterWriteStatus.Success;

            await using var existsCommand = connection.CreateCommand();
            existsCommand.CommandText = """
                SELECT Revision
                FROM UserBreakAnalysisFilters
                WHERE Id = @Id AND UserId = @UserId;
                """;
            existsCommand.Parameters.AddWithValue("@Id", id);
            existsCommand.Parameters.AddWithValue("@UserId", userId);
            var revision = await existsCommand.ExecuteScalarAsync(cancellationToken);
            return revision is null
                ? UserBreakAnalysisFilterWriteStatus.NotFound
                : UserBreakAnalysisFilterWriteStatus.RevisionConflict;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM UserBreakAnalysisFilters WHERE UserId = @UserId;";
        command.Parameters.AddWithValue("@UserId", userId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async Task<bool> NameExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long userId,
        string name,
        long? exceptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM UserBreakAnalysisFilters
                WHERE UserId = @UserId
                  AND Name = @Name COLLATE NOCASE
                  AND (@ExceptId IS NULL OR Id <> @ExceptId)
            );
            """;
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Name", name.Trim());
        command.Parameters.AddWithValue("@ExceptId", exceptId is null ? DBNull.Value : exceptId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task ClearDefaultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long userId,
        long? exceptId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE UserBreakAnalysisFilters
            SET IsDefault = 0,
                Revision = Revision + 1,
                UpdatedAtUtc = @UpdatedAtUtc
            WHERE UserId = @UserId
              AND IsDefault = 1
              AND (@ExceptId IS NULL OR Id <> @ExceptId);
            """;
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@ExceptId", (object?)exceptId ?? DBNull.Value);
        command.Parameters.AddWithValue("@UpdatedAtUtc", ToTimestamp(updatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<UserBreakAnalysisFilter?> FindAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long id,
        long userId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id, UserId, Name, VariablesJson, IsDefault, Revision,
                   CreatedAtUtc, UpdatedAtUtc
            FROM UserBreakAnalysisFilters
            WHERE Id = @Id AND UserId = @UserId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@UserId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFilter(reader) : null;
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

    private static UserBreakAnalysisFilter ReadFilter(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [],
            reader.GetInt64(4) != 0,
            reader.GetInt32(5),
            ParseTimestamp(reader.GetString(6)),
            ParseTimestamp(reader.GetString(7)));

    private static string SerializeVariables(IReadOnlyList<string> variables) =>
        JsonSerializer.Serialize(variables);

    private static string ToTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
