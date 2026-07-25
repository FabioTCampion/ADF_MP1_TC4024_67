using System.Globalization;
using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Infrastructure.Database;

public sealed class SqliteUserRepository : IUserRepository
{
    private readonly string _connectionString;

    public SqliteUserRepository(DatabaseOptions options)
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

    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ApplicationUsers;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    public async Task<int> CountActiveAdministratorsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM ApplicationUsers
            WHERE IsActive = 1 AND Role = @Role;
            """;
        command.Parameters.AddWithValue("@Role", HistorianRoles.Administrator);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ApplicationUser>> ListAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, UserName, DisplayName, PasswordHash, Role, IsActive,
                   CreatedAtUtc, LastLoginAtUtc
            FROM ApplicationUsers
            ORDER BY IsActive DESC, DisplayName COLLATE NOCASE, UserName COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<ApplicationUser>();
        while (await reader.ReadAsync(cancellationToken))
            users.Add(ReadUser(reader));
        return users;
    }

    public Task<ApplicationUser?> FindByUserNameAsync(
        string userName,
        CancellationToken cancellationToken) =>
        FindAsync("UserName = @Value COLLATE NOCASE", userName.Trim(), cancellationToken);

    public Task<ApplicationUser?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        FindAsync("Id = @Value", id, cancellationToken);

    public async Task<long> CreateAsync(
        string userName,
        string displayName,
        string passwordHash,
        string role,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ApplicationUsers
                (UserName, DisplayName, PasswordHash, Role, IsActive, CreatedAtUtc)
            VALUES
                (@UserName, @DisplayName, @PasswordHash, @Role, 1, @CreatedAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@UserName", userName.Trim());
        command.Parameters.AddWithValue("@DisplayName", displayName.Trim());
        command.Parameters.AddWithValue("@PasswordHash", passwordHash);
        command.Parameters.AddWithValue("@Role", role);
        command.Parameters.AddWithValue("@CreatedAtUtc", ToTimestamp(createdAtUtc));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    public async Task MarkLoginAsync(
        long id,
        DateTimeOffset loggedInAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ApplicationUsers
            SET LastLoginAtUtc = @LastLoginAtUtc
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@LastLoginAtUtc", ToTimestamp(loggedInAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> UpdateAsync(
        long id,
        string displayName,
        string role,
        bool isActive,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ApplicationUsers
            SET DisplayName = @DisplayName,
                Role = @Role,
                IsActive = @IsActive
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@DisplayName", displayName.Trim());
        command.Parameters.AddWithValue("@Role", role);
        command.Parameters.AddWithValue("@IsActive", isActive ? 1 : 0);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> UpdatePasswordHashAsync(
        long id,
        string passwordHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ApplicationUsers
            SET PasswordHash = @PasswordHash
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.AddWithValue("@PasswordHash", passwordHash);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<ApplicationUser?> FindAsync(
        string predicate,
        object value,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, UserName, DisplayName, PasswordHash, Role, IsActive,
                   CreatedAtUtc, LastLoginAtUtc
            FROM ApplicationUsers
            WHERE {predicate}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@Value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return ReadUser(reader);
    }

    private static ApplicationUser ReadUser(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5) == 1,
            ParseTimestamp(reader.GetString(6)),
            reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)));

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
