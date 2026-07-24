using System.Globalization;
using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Infrastructure.Database;

public sealed class SqliteHistorianRepository : IHistorianRepository
{
    private const int SchemaVersion = 3;
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteHistorianRepository(DatabaseOptions options)
    {
        options.Validate();
        SQLitePCL.Batteries_V2.Init();
        _databasePath = Path.GetFullPath(options.FilePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA synchronous=NORMAL;", cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA busy_timeout=5000;", cancellationToken);

        const string schema = """
            CREATE TABLE IF NOT EXISTS SchemaMigrations (
                Version INTEGER NOT NULL PRIMARY KEY,
                AppliedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS StatusSnapshots (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                CapturedAtUtc TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                MappingVersion TEXT NOT NULL,
                Quality TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_StatusSnapshots_CapturedAtUtc
                ON StatusSnapshots (CapturedAtUtc);

            CREATE TABLE IF NOT EXISTS StatusChanges (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                FieldName TEXT NOT NULL,
                PreviousValueJson TEXT NULL,
                CurrentValueJson TEXT NOT NULL,
                ObservedAtUtc TEXT NOT NULL,
                MappingVersion TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_StatusChanges_Field_Observed
                ON StatusChanges (FieldName, ObservedAtUtc);

            CREATE TABLE IF NOT EXISTS CommandEvents (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                CommandName TEXT NOT NULL,
                PreviousValueJson TEXT NULL,
                CurrentValueJson TEXT NOT NULL,
                ObservedAtUtc TEXT NOT NULL,
                Origin TEXT NOT NULL,
                MappingVersion TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_CommandEvents_Command_Observed
                ON CommandEvents (CommandName, ObservedAtUtc);

            CREATE TABLE IF NOT EXISTS AlarmEvents (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                AlarmName TEXT NOT NULL,
                DisplayName TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                RecommendedAction TEXT NOT NULL DEFAULT '',
                Severity TEXT NOT NULL DEFAULT 'Médio',
                Area TEXT NOT NULL DEFAULT 'Máquina',
                CatalogVersion TEXT NOT NULL DEFAULT '',
                ActivatedAtUtc TEXT NOT NULL,
                ClearedAtUtc TEXT NULL,
                DurationMilliseconds INTEGER NULL,
                ActiveAtStartup INTEGER NOT NULL,
                MappingVersion TEXT NOT NULL,
                DriveModel TEXT NULL,
                DriveFaultCode INTEGER NULL,
                DriveFaultCodeHex TEXT NULL,
                DriveFaultMnemonic TEXT NULL,
                DriveFaultTitle TEXT NULL,
                DriveFaultDescription TEXT NULL,
                DriveRecommendedAction TEXT NULL,
                DriveFaultTorque REAL NULL,
                DriveFaultEventCounter INTEGER NULL,
                ManualReference TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AlarmEvents_Alarm_Activated
                ON AlarmEvents (AlarmName, ActivatedAtUtc);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_AlarmEvents_Open
                ON AlarmEvents (AlarmName) WHERE ClearedAtUtc IS NULL;

            CREATE TABLE IF NOT EXISTS AdsCommunicationEvents (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                ObservedAtUtc TEXT NOT NULL,
                State TEXT NOT NULL,
                Detail TEXT NULL,
                AmsNetId TEXT NOT NULL,
                AdsPort INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AdsCommunicationEvents_ObservedAtUtc
                ON AdsCommunicationEvents (ObservedAtUtc);

            CREATE TABLE IF NOT EXISTS ApplicationUsers (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                UserName TEXT NOT NULL COLLATE NOCASE UNIQUE,
                DisplayName TEXT NOT NULL,
                PasswordHash TEXT NOT NULL,
                Role TEXT NOT NULL,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAtUtc TEXT NOT NULL,
                LastLoginAtUtc TEXT NULL
            );

            INSERT OR IGNORE INTO SchemaMigrations (Version, AppliedAtUtc)
            VALUES (1, @AppliedAtUtc);
            INSERT OR IGNORE INTO SchemaMigrations (Version, AppliedAtUtc)
            VALUES (2, @AppliedAtUtc);
            """;

        await ExecuteAsync(
            connection,
            null,
            schema,
            cancellationToken,
            ("@AppliedAtUtc", ToDatabaseTimestamp(DateTimeOffset.UtcNow)));

        await EnsureAlarmEventColumnsAsync(connection, cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            """
            INSERT OR IGNORE INTO SchemaMigrations (Version, AppliedAtUtc)
            VALUES (3, @AppliedAtUtc);
            """,
            cancellationToken,
            ("@AppliedAtUtc", ToDatabaseTimestamp(DateTimeOffset.UtcNow)));

        var version = await ScalarAsync<long>(
            connection,
            "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;",
            cancellationToken);
        if (version != SchemaVersion)
            throw new InvalidOperationException(
                $"Unsupported historian database schema version {version}; expected {SchemaVersion}.");
    }

    public async Task PersistCycleAsync(HistorianCycle cycle, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        if (cycle.SaveStatusSnapshot)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO StatusSnapshots
                    (CapturedAtUtc, PayloadJson, MappingVersion, Quality)
                VALUES
                    (@CapturedAtUtc, @PayloadJson, @MappingVersion, 'Good');
                """,
                cancellationToken,
                ("@CapturedAtUtc", ToDatabaseTimestamp(cycle.Snapshot.CapturedAtUtc)),
                ("@PayloadJson", cycle.Snapshot.Status.GetRawText()),
                ("@MappingVersion", cycle.Snapshot.MappingVersion));
        }

        foreach (var change in cycle.StatusChanges)
        {
            await InsertFieldChangeAsync(
                connection,
                transaction,
                "StatusChanges",
                "FieldName",
                change,
                cycle.Snapshot.MappingVersion,
                cancellationToken);
        }

        foreach (var change in cycle.CommandChanges)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO CommandEvents
                    (CommandName, PreviousValueJson, CurrentValueJson, ObservedAtUtc, Origin, MappingVersion)
                VALUES
                    (@Name, @Previous, @Current, @ObservedAtUtc, 'PlcObserved', @MappingVersion);
                """,
                cancellationToken,
                ("@Name", change.FieldName),
                ("@Previous", change.PreviousValueJson),
                ("@Current", change.CurrentValueJson),
                ("@ObservedAtUtc", ToDatabaseTimestamp(change.ObservedAtUtc)),
                ("@MappingVersion", cycle.Snapshot.MappingVersion));
        }

        foreach (var transition in cycle.AlarmTransitions)
        {
            if (transition.IsActive)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT OR IGNORE INTO AlarmEvents
                        (AlarmName, DisplayName, Description, RecommendedAction, Severity, Area, CatalogVersion,
                         ActivatedAtUtc, ClearedAtUtc, DurationMilliseconds, ActiveAtStartup, MappingVersion,
                         DriveModel, DriveFaultCode, DriveFaultCodeHex, DriveFaultMnemonic, DriveFaultTitle,
                         DriveFaultDescription, DriveRecommendedAction, DriveFaultTorque,
                         DriveFaultEventCounter, ManualReference)
                    VALUES
                        (@AlarmName, @DisplayName, @Description, @RecommendedAction, @Severity, @Area, @CatalogVersion,
                         @ObservedAtUtc, NULL, NULL, @ActiveAtStartup, @MappingVersion,
                         @DriveModel, @DriveFaultCode, @DriveFaultCodeHex, @DriveFaultMnemonic, @DriveFaultTitle,
                         @DriveFaultDescription, @DriveRecommendedAction, @DriveFaultTorque,
                         @DriveFaultEventCounter, @ManualReference);
                    """,
                    cancellationToken,
                    ("@AlarmName", transition.AlarmName),
                    ("@DisplayName", transition.Definition.DisplayName),
                    ("@Description", transition.Definition.Description),
                    ("@RecommendedAction", transition.Definition.RecommendedAction),
                    ("@Severity", transition.Definition.Severity),
                    ("@Area", transition.Definition.Area),
                    ("@CatalogVersion", transition.Definition.CatalogVersion),
                    ("@ObservedAtUtc", ToDatabaseTimestamp(transition.ObservedAtUtc)),
                    ("@ActiveAtStartup", transition.InitialObservation ? 1 : 0),
                    ("@MappingVersion", cycle.Snapshot.MappingVersion),
                    ("@DriveModel", transition.DriveFault?.Model),
                    ("@DriveFaultCode", transition.DriveFault?.Code),
                    ("@DriveFaultCodeHex", transition.DriveFault?.CodeHex),
                    ("@DriveFaultMnemonic", transition.DriveFault?.Mnemonic),
                    ("@DriveFaultTitle", transition.DriveFault?.Title),
                    ("@DriveFaultDescription", transition.DriveFault?.Description),
                    ("@DriveRecommendedAction", transition.DriveFault?.RecommendedAction),
                    ("@DriveFaultTorque", transition.DriveFault?.TorqueAtTrip),
                    ("@DriveFaultEventCounter", transition.DriveFault?.EventCounter),
                    ("@ManualReference", transition.DriveFault?.ManualReference));
            }
            else
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE AlarmEvents
                    SET ClearedAtUtc = @ObservedAtUtc,
                        DurationMilliseconds =
                            CAST(ROUND(
                                (julianday(@ObservedAtUtc) - julianday(ActivatedAtUtc)) * 86400000
                            ) AS INTEGER)
                    WHERE AlarmName = @AlarmName AND ClearedAtUtc IS NULL;
                    """,
                    cancellationToken,
                    ("@AlarmName", transition.AlarmName),
                    ("@ObservedAtUtc", ToDatabaseTimestamp(transition.ObservedAtUtc)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddCommandEventAsync(
        FieldChange change,
        string mappingVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO CommandEvents
                (CommandName, PreviousValueJson, CurrentValueJson, ObservedAtUtc, Origin, MappingVersion)
            VALUES
                (@Name, @Previous, @Current, @ObservedAtUtc, 'AdsOnChange', @MappingVersion);
            """,
            cancellationToken,
            ("@Name", change.FieldName),
            ("@Previous", change.PreviousValueJson),
            ("@Current", change.CurrentValueJson),
            ("@ObservedAtUtc", ToDatabaseTimestamp(change.ObservedAtUtc)),
            ("@MappingVersion", mappingVersion));
    }

    public async Task AddCommunicationEventAsync(
        CommunicationEvent communicationEvent,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO AdsCommunicationEvents
                (ObservedAtUtc, State, Detail, AmsNetId, AdsPort)
            VALUES
                (@ObservedAtUtc, @State, @Detail, @AmsNetId, @AdsPort);
            """,
            cancellationToken,
            ("@ObservedAtUtc", ToDatabaseTimestamp(communicationEvent.ObservedAtUtc)),
            ("@State", communicationEvent.State),
            ("@Detail", communicationEvent.Detail),
            ("@AmsNetId", communicationEvent.AmsNetId),
            ("@AdsPort", communicationEvent.AdsPort));
    }

    public async Task<IReadOnlyList<StatusSnapshotRow>> GetStatusSnapshotsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateRangeCommand(
            connection,
            """
            SELECT Id, CapturedAtUtc, PayloadJson, MappingVersion, Quality
            FROM StatusSnapshots
            WHERE (@FromUtc IS NULL OR CapturedAtUtc >= @FromUtc)
              AND (@ToUtc IS NULL OR CapturedAtUtc < @ToUtc)
            ORDER BY CapturedAtUtc DESC
            LIMIT @Limit;
            """,
            fromUtc,
            toUtc,
            limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<StatusSnapshotRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new StatusSnapshotRow(
                reader.GetInt64(0),
                ParseDatabaseTimestamp(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<StatusSnapshotRow>> GetStatusTrendSamplesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maximumPoints,
        CancellationToken cancellationToken)
    {
        if (fromUtc >= toUtc)
            throw new ArgumentException("The trend start must be earlier than its end.");
        if (maximumPoints is < 100 or > 2_000)
            throw new ArgumentOutOfRangeException(
                nameof(maximumPoints),
                "Maximum points must be between 100 and 2000.");

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateRangeCommand(
            connection,
            """
            WITH BucketedSnapshots AS (
                SELECT Id, CapturedAtUtc, PayloadJson, MappingVersion, Quality,
                       NTILE(@MaximumPoints) OVER (ORDER BY CapturedAtUtc) AS Bucket,
                       ROW_NUMBER() OVER (ORDER BY CapturedAtUtc DESC) AS ReverseNumber
                FROM StatusSnapshots
                WHERE CapturedAtUtc >= @FromUtc
                  AND CapturedAtUtc < @ToUtc
            ),
            SampledSnapshots AS (
                SELECT Id, CapturedAtUtc, PayloadJson, MappingVersion, Quality,
                       ReverseNumber,
                       ROW_NUMBER() OVER (
                           PARTITION BY Bucket
                           ORDER BY CapturedAtUtc
                       ) AS BucketRow
                FROM BucketedSnapshots
            )
            SELECT Id, CapturedAtUtc, PayloadJson, MappingVersion, Quality
            FROM SampledSnapshots
            WHERE BucketRow = 1 OR ReverseNumber = 1
            ORDER BY CapturedAtUtc;
            """,
            fromUtc,
            toUtc,
            maximumPoints);
        command.Parameters.AddWithValue("@MaximumPoints", maximumPoints);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<StatusSnapshotRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new StatusSnapshotRow(
                reader.GetInt64(0),
                ParseDatabaseTimestamp(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<MachineProductivitySampleRow>> GetMachineProductivitySamplesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        if (fromUtc >= toUtc)
            throw new ArgumentException("The productivity start must be earlier than its end.");

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateRangeCommand(
            connection,
            """
            SELECT CapturedAtUtc,
                   json_extract(
                       PayloadJson,
                       '$.dryingSectionGroup3UpperMasterSpeedMPM'),
                   json_extract(
                       PayloadJson,
                       '$.dryingSectionGroup3PaperPresence'),
                   Quality
            FROM StatusSnapshots
            WHERE CapturedAtUtc >= @FromUtc
              AND CapturedAtUtc < @ToUtc
            ORDER BY CapturedAtUtc;
            """,
            fromUtc,
            toUtc,
            limit: 1);
        command.Parameters.RemoveAt("@Limit");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<MachineProductivitySampleRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MachineProductivitySampleRow(
                ParseDatabaseTimestamp(reader.GetString(0)),
                reader.IsDBNull(1)
                    ? null
                    : Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.IsDBNull(2)
                    ? null
                    : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture) != 0,
                reader.GetString(3)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<CommandEventRow>> GetCommandEventsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateRangeCommand(
            connection,
            """
            SELECT Id, CommandName, PreviousValueJson, CurrentValueJson,
                   ObservedAtUtc, Origin, MappingVersion
            FROM CommandEvents
            WHERE (@FromUtc IS NULL OR ObservedAtUtc >= @FromUtc)
              AND (@ToUtc IS NULL OR ObservedAtUtc < @ToUtc)
            ORDER BY ObservedAtUtc DESC
            LIMIT @Limit;
            """,
            fromUtc,
            toUtc,
            limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<CommandEventRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CommandEventRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                ParseDatabaseTimestamp(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<StatusChangeRow>> GetStatusChangesAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateRangeCommand(
            connection,
            """
            SELECT Id, FieldName, PreviousValueJson, CurrentValueJson,
                   ObservedAtUtc, MappingVersion
            FROM StatusChanges
            WHERE (@FromUtc IS NULL OR ObservedAtUtc >= @FromUtc)
              AND (@ToUtc IS NULL OR ObservedAtUtc < @ToUtc)
            ORDER BY ObservedAtUtc DESC
            LIMIT @Limit;
            """,
            fromUtc,
            toUtc,
            limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<StatusChangeRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new StatusChangeRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                ParseDatabaseTimestamp(reader.GetString(4)),
                reader.GetString(5)));
        }
        return rows;
    }

    public async Task<IReadOnlyList<AlarmEventRow>> GetAlarmEventsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        bool? active,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateRangeCommand(
            connection,
            """
            SELECT Id, AlarmName, DisplayName, Description, RecommendedAction,
                   Severity, Area, CatalogVersion, ActivatedAtUtc, ClearedAtUtc,
                   DurationMilliseconds, ActiveAtStartup, MappingVersion,
                   DriveModel, DriveFaultCode, DriveFaultCodeHex, DriveFaultMnemonic,
                   DriveFaultTitle, DriveFaultDescription, DriveRecommendedAction,
                   DriveFaultTorque, DriveFaultEventCounter, ManualReference
            FROM AlarmEvents
            WHERE (@FromUtc IS NULL OR ActivatedAtUtc >= @FromUtc)
              AND (@ToUtc IS NULL OR ActivatedAtUtc < @ToUtc)
              AND (@Active IS NULL
                   OR (@Active = 1 AND ClearedAtUtc IS NULL)
                   OR (@Active = 0 AND ClearedAtUtc IS NOT NULL))
            ORDER BY ActivatedAtUtc DESC
            LIMIT @Limit;
            """,
            fromUtc,
            toUtc,
            limit);
        command.Parameters.AddWithValue("@Active", active.HasValue ? (active.Value ? 1 : 0) : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<AlarmEventRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var alarmName = reader.GetString(1);
            var fallback = AlarmCatalog.Resolve(alarmName);
            var storedCatalogVersion = reader.IsDBNull(7) ? null : reader.GetString(7);
            var hasStoredCatalog = !string.IsNullOrWhiteSpace(storedCatalogVersion);
            rows.Add(new AlarmEventRow(
                reader.GetInt64(0),
                alarmName,
                hasStoredCatalog ? ReadCatalogText(reader, 2, fallback.DisplayName) : fallback.DisplayName,
                hasStoredCatalog ? ReadCatalogText(reader, 3, fallback.Description) : fallback.Description,
                hasStoredCatalog ? ReadCatalogText(reader, 4, fallback.RecommendedAction) : fallback.RecommendedAction,
                hasStoredCatalog ? ReadCatalogText(reader, 5, fallback.Severity) : fallback.Severity,
                hasStoredCatalog ? ReadCatalogText(reader, 6, fallback.Area) : fallback.Area,
                hasStoredCatalog ? storedCatalogVersion! : fallback.CatalogVersion,
                ParseDatabaseTimestamp(reader.GetString(8)),
                reader.IsDBNull(9) ? null : ParseDatabaseTimestamp(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.GetInt64(11) == 1,
                reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetInt32(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetDouble(20),
                reader.IsDBNull(21) ? null : reader.GetInt64(21),
                reader.IsDBNull(22) ? null : reader.GetString(22)));
        }
        return rows;
    }

    private static string ReadCatalogText(SqliteDataReader reader, int ordinal, string fallback) =>
        reader.IsDBNull(ordinal) || string.IsNullOrWhiteSpace(reader.GetString(ordinal))
            ? fallback
            : reader.GetString(ordinal);

    private static async Task EnsureAlarmEventColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(AlarmEvents);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                existing.Add(reader.GetString(1));
        }

        var required = new (string Name, string Sql)[]
        {
            ("DisplayName", "TEXT NOT NULL DEFAULT ''"),
            ("Description", "TEXT NOT NULL DEFAULT ''"),
            ("RecommendedAction", "TEXT NOT NULL DEFAULT ''"),
            ("Severity", "TEXT NOT NULL DEFAULT 'Médio'"),
            ("Area", "TEXT NOT NULL DEFAULT 'Máquina'"),
            ("CatalogVersion", "TEXT NOT NULL DEFAULT ''"),
            ("DriveModel", "TEXT NULL"),
            ("DriveFaultCode", "INTEGER NULL"),
            ("DriveFaultCodeHex", "TEXT NULL"),
            ("DriveFaultMnemonic", "TEXT NULL"),
            ("DriveFaultTitle", "TEXT NULL"),
            ("DriveFaultDescription", "TEXT NULL"),
            ("DriveRecommendedAction", "TEXT NULL"),
            ("DriveFaultTorque", "REAL NULL"),
            ("DriveFaultEventCounter", "INTEGER NULL"),
            ("ManualReference", "TEXT NULL")
        };

        foreach (var column in required)
        {
            if (existing.Contains(column.Name))
                continue;
            await ExecuteAsync(
                connection,
                null,
                $"ALTER TABLE AlarmEvents ADD COLUMN {column.Name} {column.Sql};",
                cancellationToken);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA busy_timeout=5000;", cancellationToken);
        return connection;
    }

    private static async Task InsertFieldChangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string nameColumn,
        FieldChange change,
        string mappingVersion,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            $"""
             INSERT INTO {table}
                 ({nameColumn}, PreviousValueJson, CurrentValueJson, ObservedAtUtc, MappingVersion)
             VALUES
                 (@Name, @Previous, @Current, @ObservedAtUtc, @MappingVersion);
             """,
            cancellationToken,
            ("@Name", change.FieldName),
            ("@Previous", change.PreviousValueJson),
            ("@Current", change.CurrentValueJson),
            ("@ObservedAtUtc", ToDatabaseTimestamp(change.ObservedAtUtc)),
            ("@MappingVersion", mappingVersion));
    }

    private static SqliteCommand CreateRangeCommand(
        SqliteConnection connection,
        string sql,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(
            "@FromUtc",
            fromUtc.HasValue ? ToDatabaseTimestamp(fromUtc.Value) : DBNull.Value);
        command.Parameters.AddWithValue(
            "@ToUtc",
            toUtc.HasValue ? ToDatabaseTimestamp(toUtc.Value) : DBNull.Value);
        command.Parameters.AddWithValue("@Limit", limit);
        return command;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static string ToDatabaseTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDatabaseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 5_000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 5000.");
    }
}
