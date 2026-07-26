using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Infrastructure.Database;

public sealed class SqliteHistorianRepository : IHistorianRepository
{
    private const int SchemaVersion = 5;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

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
        await EnsureOptimizedHistorianSchemaAsync(connection, cancellationToken);
        await EnsurePaperBreakDiagnosticSchemaAsync(connection, cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            """
            INSERT OR IGNORE INTO SchemaMigrations (Version, AppliedAtUtc)
            VALUES (3, @AppliedAtUtc);
            INSERT OR IGNORE INTO SchemaMigrations (Version, AppliedAtUtc)
            VALUES (4, @AppliedAtUtc);
            INSERT OR IGNORE INTO SchemaMigrations (Version, AppliedAtUtc)
            VALUES (5, @AppliedAtUtc);
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
        if (!cycle.SaveTelemetrySample &&
            !cycle.SaveStatusSnapshot &&
            cycle.StatusChanges.Count == 0 &&
            cycle.CommandChanges.Count == 0 &&
            cycle.AlarmTransitions.Count == 0 &&
            cycle.PaperBreakTransitions.Count == 0 &&
            cycle.PaperBreakDiagnostics.Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction();

            if (cycle.SaveTelemetrySample)
            {
                var telemetry = TelemetryCatalog.Extract(cycle.Snapshot.Status);
                await InsertTelemetrySampleAsync(
                    connection,
                    transaction,
                    cycle.Snapshot.CapturedAtUtc,
                    telemetry,
                    cycle.Snapshot.MappingVersion,
                    cancellationToken);
            }

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

        foreach (var transition in cycle.PaperBreakTransitions)
        {
            if (transition.IsActive)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT OR IGNORE INTO PaperBreakEvents
                        (StartedAtUnixMs, EndedAtUnixMs, DurationMilliseconds,
                         ActiveAtStartup, SpeedAtStartMpm, SpeedAtEndMpm, MappingVersion)
                    VALUES
                        (@StartedAtUnixMs, NULL, NULL, @ActiveAtStartup,
                         @SpeedAtStartMpm, NULL, @MappingVersion);
                    """,
                    cancellationToken,
                    ("@StartedAtUnixMs", transition.ObservedAtUtc.ToUnixTimeMilliseconds()),
                    ("@ActiveAtStartup", transition.InitialObservation ? 1 : 0),
                    ("@SpeedAtStartMpm", transition.SpeedMpm),
                    ("@MappingVersion", cycle.Snapshot.MappingVersion));

                var diagnostic = cycle.PaperBreakDiagnostics.FirstOrDefault(item =>
                    item.BreakAtUtc == transition.ObservedAtUtc);
                if (diagnostic is not null)
                {
                    var paperBreakEventId = await GetPaperBreakEventIdAsync(
                        connection,
                        transaction,
                        transition.ObservedAtUtc,
                        cancellationToken);
                    if (paperBreakEventId.HasValue)
                    {
                        await InsertPaperBreakDiagnosticAsync(
                            connection,
                            transaction,
                            paperBreakEventId.Value,
                            diagnostic,
                            cancellationToken);
                        await InsertPaperBreakEvidenceAsync(
                            connection,
                            transaction,
                            paperBreakEventId.Value,
                            diagnostic.BreakAtUtc,
                            cancellationToken);
                    }
                }
            }
            else
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE PaperBreakEvents
                    SET EndedAtUnixMs = @EndedAtUnixMs,
                        DurationMilliseconds = MAX(0, @EndedAtUnixMs - StartedAtUnixMs),
                        SpeedAtEndMpm = @SpeedAtEndMpm
                    WHERE EndedAtUnixMs IS NULL;
                    """,
                    cancellationToken,
                    ("@EndedAtUnixMs", transition.ObservedAtUtc.ToUnixTimeMilliseconds()),
                    ("@SpeedAtEndMpm", transition.SpeedMpm));
            }
        }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task AddCommandEventAsync(
        FieldChange change,
        string mappingVersion,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
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
            await ExecuteAsync(
                connection,
                null,
                """
                INSERT OR IGNORE INTO PaperBreakEvidence
                    (PaperBreakEventId, Kind, Name, PreviousValueJson, CurrentValueJson,
                     ObservedAtUtc, OffsetMilliseconds, Description, Severity)
                SELECT Id, 'Comando', @Name, @Previous, @Current, @ObservedAtUtc,
                       @ObservedAtUnixMs - StartedAtUnixMs, 'AdsOnChange', NULL
                FROM PaperBreakEvents
                WHERE ActiveAtStartup = 0
                  AND StartedAtUnixMs >= @ObservedAtUnixMs
                  AND StartedAtUnixMs <= @ObservedAtUnixMs + 180000;
                """,
                cancellationToken,
                ("@Name", change.FieldName),
                ("@Previous", change.PreviousValueJson),
                ("@Current", change.CurrentValueJson),
                ("@ObservedAtUtc", ToDatabaseTimestamp(change.ObservedAtUtc)),
                ("@ObservedAtUnixMs", change.ObservedAtUtc.ToUnixTimeMilliseconds()));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task AddCommunicationEventAsync(
        CommunicationEvent communicationEvent,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
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
        finally
        {
            _writeGate.Release();
        }
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

    public async Task<IReadOnlyList<TelemetrySampleRow>> GetTelemetryTrendSamplesAsync(
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

        var useMinuteAggregates = toUtc - fromUtc > TimeSpan.FromHours(12);
        var optimizedRows = await ReadOptimizedTelemetryAsync(
            fromUtc,
            toUtc,
            useMinuteAggregates,
            cancellationToken);
        var firstOptimizedAt = optimizedRows.Count > 0
            ? optimizedRows[0].CapturedAtUtc
            : toUtc;
        var legacyRows = new List<TelemetrySampleRow>();

        if (fromUtc < firstOptimizedAt)
        {
            var legacySnapshots = await GetStatusTrendSamplesAsync(
                fromUtc,
                firstOptimizedAt,
                maximumPoints,
                cancellationToken);
            foreach (var snapshot in legacySnapshots)
            {
                using var document = JsonDocument.Parse(snapshot.PayloadJson);
                var values = TelemetryCatalog.Extract(document.RootElement);
                legacyRows.Add(new TelemetrySampleRow(
                    snapshot.CapturedAtUtc,
                    values.Numeric,
                    values.Boolean,
                    snapshot.MappingVersion,
                    snapshot.Quality));
            }
        }

        return Downsample(
            legacyRows
                .Concat(optimizedRows)
                .OrderBy(row => row.CapturedAtUtc)
                .ToArray(),
            maximumPoints);
    }

    public async Task<IReadOnlyList<MachineProductivitySampleRow>> GetMachineProductivitySamplesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        if (fromUtc >= toUtc)
            throw new ArgumentException("The productivity start must be earlier than its end.");

        var rows = new List<MachineProductivitySampleRow>();
        var useMinuteAggregates = toUtc - fromUtc > TimeSpan.FromHours(12);
        await using (var connection = await OpenAsync(cancellationToken))
        {
            var source = useMinuteAggregates
                ? "TelemetryMinuteAggregates"
                : "TelemetrySamples";
            var timestampColumn = useMinuteAggregates
                ? "BucketUnixMs"
                : "CapturedAtUnixMs";
            var paperField = QuoteIdentifier(TelemetryCatalog.PaperPresenceField);
            var speedField = useMinuteAggregates
                ? QuoteIdentifier(TelemetryCatalog.MachineSpeedField)
                : QuoteIdentifier(TelemetryCatalog.MachineSpeedField);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {timestampColumn}, {speedField}, {paperField}, Quality
                FROM {source}
                WHERE {timestampColumn} >= @FromUnixMs
                  AND {timestampColumn} < @ToUnixMs
                ORDER BY {timestampColumn};
                """;
            command.Parameters.AddWithValue("@FromUnixMs", fromUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@ToUnixMs", toUtc.ToUnixTimeMilliseconds());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                double? paperValue = reader.IsDBNull(2) ? null : reader.GetDouble(2);
                rows.Add(new MachineProductivitySampleRow(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                    reader.IsDBNull(1) ? null : reader.GetDouble(1),
                    paperValue.HasValue ? paperValue.Value >= 0.5 : null,
                    reader.GetString(3)));
            }
        }

        var firstOptimizedAt = rows.Count > 0 ? rows[0].CapturedAtUtc : toUtc;
        if (fromUtc < firstOptimizedAt)
            rows.InsertRange(0, await ReadLegacyProductivityAsync(
                fromUtc,
                firstOptimizedAt,
                cancellationToken));

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

    public async Task<HistoryPage<CommandEventRow>> SearchCommandEventsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? search,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateHistoryPage(fromUtc, toUtc, offset, limit);
        await using var connection = await OpenAsync(cancellationToken);
        const string where = """
            WHERE ObservedAtUtc >= @FromUtc
              AND ObservedAtUtc < @ToUtc
              AND (@Search IS NULL
                   OR CommandName COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR COALESCE(PreviousValueJson, '') COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR CurrentValueJson COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR Origin COLLATE NOCASE LIKE @Search ESCAPE '\')
            """;

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM CommandEvents {where};";
        AddHistorySearchParameters(countCommand, fromUtc, toUtc, search);
        var total = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, CommandName, PreviousValueJson, CurrentValueJson,
                   ObservedAtUtc, Origin, MappingVersion
            FROM CommandEvents
            {where}
            ORDER BY ObservedAtUtc DESC
            LIMIT @Limit OFFSET @Offset;
            """;
        AddHistorySearchParameters(command, fromUtc, toUtc, search);
        command.Parameters.AddWithValue("@Limit", limit);
        command.Parameters.AddWithValue("@Offset", offset);

        var rows = new List<CommandEventRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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
        return new HistoryPage<CommandEventRow>(rows, total, offset, limit);
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

    public async Task<HistoryPage<StatusChangeRow>> SearchStatusChangesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? search,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateHistoryPage(fromUtc, toUtc, offset, limit);
        await using var connection = await OpenAsync(cancellationToken);
        const string where = """
            WHERE ObservedAtUtc >= @FromUtc
              AND ObservedAtUtc < @ToUtc
              AND (@Search IS NULL
                   OR FieldName COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR COALESCE(PreviousValueJson, '') COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR CurrentValueJson COLLATE NOCASE LIKE @Search ESCAPE '\')
            """;

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM StatusChanges {where};";
        AddHistorySearchParameters(countCommand, fromUtc, toUtc, search);
        var total = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, FieldName, PreviousValueJson, CurrentValueJson,
                   ObservedAtUtc, MappingVersion
            FROM StatusChanges
            {where}
            ORDER BY ObservedAtUtc DESC
            LIMIT @Limit OFFSET @Offset;
            """;
        AddHistorySearchParameters(command, fromUtc, toUtc, search);
        command.Parameters.AddWithValue("@Limit", limit);
        command.Parameters.AddWithValue("@Offset", offset);

        var rows = new List<StatusChangeRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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
        return new HistoryPage<StatusChangeRow>(rows, total, offset, limit);
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

    public async Task<HistoryPage<AlarmEventRow>> SearchAlarmEventsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        bool? active,
        string? search,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateHistoryPage(fromUtc, toUtc, offset, limit);
        await using var connection = await OpenAsync(cancellationToken);
        const string where = """
            WHERE ActivatedAtUtc >= @FromUtc
              AND ActivatedAtUtc < @ToUtc
              AND (@Active IS NULL
                   OR (@Active = 1 AND ClearedAtUtc IS NULL)
                   OR (@Active = 0 AND ClearedAtUtc IS NOT NULL))
              AND (@Search IS NULL
                   OR AlarmName COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR DisplayName COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR Description COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR RecommendedAction COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR Severity COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR Area COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR COALESCE(DriveFaultCodeHex, '') COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR COALESCE(DriveFaultMnemonic, '') COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR COALESCE(DriveFaultTitle, '') COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR COALESCE(DriveFaultDescription, '') COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR COALESCE(DriveRecommendedAction, '') COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR CAST(COALESCE(DriveFaultCode, '') AS TEXT) LIKE @Search ESCAPE '\')
            """;

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM AlarmEvents {where};";
        AddHistorySearchParameters(countCommand, fromUtc, toUtc, search);
        countCommand.Parameters.AddWithValue(
            "@Active",
            active.HasValue ? (active.Value ? 1 : 0) : DBNull.Value);
        var total = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, AlarmName, DisplayName, Description, RecommendedAction,
                   Severity, Area, CatalogVersion, ActivatedAtUtc, ClearedAtUtc,
                   DurationMilliseconds, ActiveAtStartup, MappingVersion,
                   DriveModel, DriveFaultCode, DriveFaultCodeHex, DriveFaultMnemonic,
                   DriveFaultTitle, DriveFaultDescription, DriveRecommendedAction,
                   DriveFaultTorque, DriveFaultEventCounter, ManualReference
            FROM AlarmEvents
            {where}
            ORDER BY ActivatedAtUtc DESC
            LIMIT @Limit OFFSET @Offset;
            """;
        AddHistorySearchParameters(command, fromUtc, toUtc, search);
        command.Parameters.AddWithValue(
            "@Active",
            active.HasValue ? (active.Value ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("@Limit", limit);
        command.Parameters.AddWithValue("@Offset", offset);

        var rows = new List<AlarmEventRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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
        return new HistoryPage<AlarmEventRow>(rows, total, offset, limit);
    }

    public async Task<IReadOnlyList<PaperBreakEventRow>> GetPaperBreakEventsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, StartedAtUnixMs, EndedAtUnixMs, DurationMilliseconds,
                   ActiveAtStartup, SpeedAtStartMpm, SpeedAtEndMpm, MappingVersion,
                   (SELECT COUNT(*) FROM PaperBreakDiagnosticSamples samples
                    WHERE samples.PaperBreakEventId = events.Id),
                   AnalysisStatus, CauseCategory, CauseDescription, AnalysisNotes,
                   AnalyzedBy, AnalyzedAtUtc
            FROM PaperBreakEvents events
            WHERE (@FromUnixMs IS NULL OR StartedAtUnixMs >= @FromUnixMs)
              AND (@ToUnixMs IS NULL OR StartedAtUnixMs < @ToUnixMs)
            ORDER BY StartedAtUnixMs DESC
            LIMIT @Limit;
            """;
        command.Parameters.AddWithValue(
            "@FromUnixMs",
            fromUtc.HasValue ? fromUtc.Value.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue(
            "@ToUnixMs",
            toUtc.HasValue ? toUtc.Value.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("@Limit", limit);

        var rows = new List<PaperBreakEventRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PaperBreakEventRow(
                reader.GetInt64(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.IsDBNull(2)
                    ? null
                    : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetInt64(4) != 0,
                reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : ParseDatabaseTimestamp(reader.GetString(14))));
        }

        return rows;
    }

    public async Task<HistoryPage<PaperBreakEventRow>> SearchPaperBreakEventsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? analysisStatus,
        string? causeCategory,
        string? search,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        ValidateHistoryPage(fromUtc, toUtc, offset, limit);
        await using var connection = await OpenAsync(cancellationToken);
        const string where = """
            WHERE StartedAtUnixMs >= @FromUnixMs
              AND StartedAtUnixMs < @ToUnixMs
              AND (@AnalysisStatus IS NULL OR AnalysisStatus = @AnalysisStatus)
              AND (@CauseCategory IS NULL OR CauseCategory = @CauseCategory)
              AND (@Search IS NULL
                   OR CAST(Id AS TEXT) LIKE @Search ESCAPE '\'
                   OR AnalysisStatus COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR COALESCE(CauseCategory, '') COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR COALESCE(CauseDescription, '') COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR COALESCE(AnalysisNotes, '') COLLATE NOCASE LIKE @Search ESCAPE '\'
                   OR COALESCE(AnalyzedBy, '') COLLATE NOCASE LIKE @Search ESCAPE '\')
            """;

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM PaperBreakEvents {where};";
        AddPaperBreakSearchParameters(
            countCommand,
            fromUtc,
            toUtc,
            analysisStatus,
            causeCategory,
            search);
        var total = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, StartedAtUnixMs, EndedAtUnixMs, DurationMilliseconds,
                   ActiveAtStartup, SpeedAtStartMpm, SpeedAtEndMpm, MappingVersion,
                   (SELECT COUNT(*) FROM PaperBreakDiagnosticSamples samples
                    WHERE samples.PaperBreakEventId = events.Id),
                   AnalysisStatus, CauseCategory, CauseDescription, AnalysisNotes,
                   AnalyzedBy, AnalyzedAtUtc
            FROM PaperBreakEvents events
            {where}
            ORDER BY StartedAtUnixMs DESC
            LIMIT @Limit OFFSET @Offset;
            """;
        AddPaperBreakSearchParameters(
            command,
            fromUtc,
            toUtc,
            analysisStatus,
            causeCategory,
            search);
        command.Parameters.AddWithValue("@Limit", limit);
        command.Parameters.AddWithValue("@Offset", offset);

        var rows = new List<PaperBreakEventRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(ReadPaperBreakEvent(reader));
        return new HistoryPage<PaperBreakEventRow>(rows, total, offset, limit);
    }

    public async Task<PaperBreakDiagnosticRow?> GetPaperBreakDiagnosticAsync(
        long paperBreakEventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        PaperBreakEventRow? paperBreakEvent;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, StartedAtUnixMs, EndedAtUnixMs, DurationMilliseconds,
                       ActiveAtStartup, SpeedAtStartMpm, SpeedAtEndMpm, MappingVersion,
                       (SELECT COUNT(*) FROM PaperBreakDiagnosticSamples samples
                        WHERE samples.PaperBreakEventId = events.Id),
                       AnalysisStatus, CauseCategory, CauseDescription, AnalysisNotes,
                       AnalyzedBy, AnalyzedAtUtc
                FROM PaperBreakEvents events
                WHERE Id = @Id;
                """;
            command.Parameters.AddWithValue("@Id", paperBreakEventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            paperBreakEvent = ReadPaperBreakEvent(reader);
        }

        var samples = new List<PaperBreakDiagnosticSampleRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT CapturedAtUnixMs, OffsetMilliseconds, StatusJson, Quality
                FROM PaperBreakDiagnosticSamples
                WHERE PaperBreakEventId = @Id
                ORDER BY CapturedAtUnixMs;
                """;
            command.Parameters.AddWithValue("@Id", paperBreakEventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                samples.Add(new PaperBreakDiagnosticSampleRow(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        var summary = new List<PaperBreakDiagnosticSummaryRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT FieldName, Category, Unit, SampleCount, Minimum, Maximum, Average,
                       StandardDeviation, ValueAtBreak, BaselineAverage, CriticalAverage,
                       Delta, AnomalyScore
                FROM PaperBreakDiagnosticSummary
                WHERE PaperBreakEventId = @Id
                ORDER BY COALESCE(AnomalyScore, -1) DESC, FieldName;
                """;
            command.Parameters.AddWithValue("@Id", paperBreakEventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                summary.Add(new PaperBreakDiagnosticSummaryRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    ReadNullableDouble(reader, 4),
                    ReadNullableDouble(reader, 5),
                    ReadNullableDouble(reader, 6),
                    ReadNullableDouble(reader, 7),
                    ReadNullableDouble(reader, 8),
                    ReadNullableDouble(reader, 9),
                    ReadNullableDouble(reader, 10),
                    ReadNullableDouble(reader, 11),
                    ReadNullableDouble(reader, 12)));
            }
        }

        var evidence = new List<PaperBreakEvidenceRow>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, Kind, Name, PreviousValueJson, CurrentValueJson,
                       ObservedAtUtc, OffsetMilliseconds, Description, Severity
                FROM PaperBreakEvidence
                WHERE PaperBreakEventId = @Id
                ORDER BY ObservedAtUtc;
                """;
            command.Parameters.AddWithValue("@Id", paperBreakEventId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                evidence.Add(new PaperBreakEvidenceRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    ParseDatabaseTimestamp(reader.GetString(5)),
                    reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        return new PaperBreakDiagnosticRow(paperBreakEvent, samples, summary, evidence);
    }

    public async Task<bool> UpdatePaperBreakAnalysisAsync(
        long paperBreakEventId,
        PaperBreakAnalysisUpdate update,
        string analyzedBy,
        DateTimeOffset analyzedAtUtc,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE PaperBreakEvents
                SET AnalysisStatus = @AnalysisStatus,
                    CauseCategory = @CauseCategory,
                    CauseDescription = @CauseDescription,
                    AnalysisNotes = @AnalysisNotes,
                    AnalyzedBy = @AnalyzedBy,
                    AnalyzedAtUtc = @AnalyzedAtUtc
                WHERE Id = @Id;
                """;
            command.Parameters.AddWithValue("@Id", paperBreakEventId);
            command.Parameters.AddWithValue("@AnalysisStatus", update.AnalysisStatus);
            command.Parameters.AddWithValue(
                "@CauseCategory",
                (object?)update.CauseCategory ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@CauseDescription",
                (object?)update.CauseDescription ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@AnalysisNotes",
                (object?)update.AnalysisNotes ?? DBNull.Value);
            command.Parameters.AddWithValue("@AnalyzedBy", analyzedBy);
            command.Parameters.AddWithValue(
                "@AnalyzedAtUtc",
                ToDatabaseTimestamp(analyzedAtUtc));
            return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<HistorianMaintenanceResult> RunMaintenanceAsync(
        DateTimeOffset nowUtc,
        HistorianOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);

        var lastBackfilledId = await ReadMaintenanceLongAsync(
            connection,
            "LegacyBackfillLastId",
            cancellationToken);
        var backfilled = await BackfillLegacySnapshotsAsync(
            connection,
            lastBackfilledId,
            Math.Min(options.MaintenanceBatchSize, 250),
            nowUtc,
            cancellationToken);
        if (backfilled.LastId > lastBackfilledId)
            lastBackfilledId = backfilled.LastId;

        var deletedSnapshots = 0;
        var deletedTelemetry = 0;
        var deletedAggregates = 0;
        var deletedStatusChanges = 0;
        var deletedAnalogChanges = 0;
        var deletedCommunicationEvents = 0;
        var hasMatureOptimizedTelemetry = await HasMatureOptimizedTelemetryAsync(
            connection,
            nowUtc.AddHours(-24),
            cancellationToken);

        if (options.RetentionEnabled && hasMatureOptimizedTelemetry)
        {
            deletedSnapshots = await DeleteBatchAsync(
                connection,
                """
                DELETE FROM StatusSnapshots
                WHERE Id IN (
                    SELECT Id
                    FROM StatusSnapshots
                    WHERE Id <= @LastBackfilledId
                      AND CapturedAtUtc < @Cutoff
                    ORDER BY Id
                    LIMIT @BatchSize
                );
                """,
                cancellationToken,
                ("@LastBackfilledId", lastBackfilledId),
                ("@Cutoff", ToDatabaseTimestamp(
                    nowUtc.AddDays(-options.DiagnosticSnapshotRetentionDays))),
                ("@BatchSize", options.MaintenanceBatchSize));
            deletedTelemetry = await DeleteBatchAsync(
                connection,
                """
                DELETE FROM TelemetrySamples
                WHERE Id IN (
                    SELECT Id
                    FROM TelemetrySamples
                    WHERE CapturedAtUnixMs < @CutoffUnixMs
                    ORDER BY Id
                    LIMIT @BatchSize
                );
                """,
                cancellationToken,
                ("@CutoffUnixMs", nowUtc
                    .AddDays(-options.RawTelemetryRetentionDays)
                    .ToUnixTimeMilliseconds()),
                ("@BatchSize", options.MaintenanceBatchSize));
            deletedAggregates = await DeleteBatchAsync(
                connection,
                """
                DELETE FROM TelemetryMinuteAggregates
                WHERE BucketUnixMs IN (
                    SELECT BucketUnixMs
                    FROM TelemetryMinuteAggregates
                    WHERE BucketUnixMs < @CutoffUnixMs
                    ORDER BY BucketUnixMs
                    LIMIT @BatchSize
                );
                """,
                cancellationToken,
                ("@CutoffUnixMs", nowUtc
                    .AddDays(-options.AggregateRetentionDays)
                    .ToUnixTimeMilliseconds()),
                ("@BatchSize", options.MaintenanceBatchSize));
            deletedStatusChanges = await DeleteBatchAsync(
                connection,
                """
                DELETE FROM StatusChanges
                WHERE Id IN (
                    SELECT Id
                    FROM StatusChanges
                    WHERE ObservedAtUtc < @Cutoff
                    ORDER BY Id
                    LIMIT @BatchSize
                );
                """,
                cancellationToken,
                ("@Cutoff", ToDatabaseTimestamp(
                    nowUtc.AddDays(-options.StatusChangeRetentionDays))),
                ("@BatchSize", options.MaintenanceBatchSize));
            deletedAnalogChanges = await DeleteBatchAsync(
                connection,
                """
                DELETE FROM StatusChanges
                WHERE Id IN (
                    SELECT Id
                    FROM StatusChanges
                    WHERE CurrentValueJson NOT IN ('true', 'false')
                      AND FieldName NOT LIKE '%State'
                      AND FieldName NOT LIKE '%FaultCode'
                      AND FieldName NOT LIKE '%FaultEventCounter'
                    ORDER BY Id
                    LIMIT @BatchSize
                );
                """,
                cancellationToken,
                ("@BatchSize", options.MaintenanceBatchSize));
            deletedCommunicationEvents = await DeleteBatchAsync(
                connection,
                """
                DELETE FROM AdsCommunicationEvents
                WHERE Id IN (
                    SELECT Id
                    FROM AdsCommunicationEvents
                    WHERE ObservedAtUtc < @Cutoff
                    ORDER BY Id
                    LIMIT @BatchSize
                );
                """,
                cancellationToken,
                ("@Cutoff", ToDatabaseTimestamp(
                    nowUtc.AddDays(-options.CommunicationEventRetentionDays))),
                ("@BatchSize", options.MaintenanceBatchSize));
        }

        await WriteMaintenanceStateAsync(
            connection,
            "LastMaintenanceAtUtc",
            ToDatabaseTimestamp(nowUtc),
            nowUtc,
            cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA optimize;", cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA wal_checkpoint(PASSIVE);", cancellationToken);

            return new HistorianMaintenanceResult(
                deletedSnapshots,
                deletedTelemetry,
                deletedAggregates,
                deletedStatusChanges,
                deletedAnalogChanges,
                deletedCommunicationEvents,
                nowUtc);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<HistorianStorageStatus> GetStorageStatusAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var pageSize = await ScalarAsync<long>(connection, "PRAGMA page_size;", cancellationToken);
        var reusablePages = await ScalarAsync<long>(
            connection,
            "PRAGMA freelist_count;",
            cancellationToken);
        var telemetryCount = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM TelemetrySamples;",
            cancellationToken);
        var snapshotCount = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM StatusSnapshots;",
            cancellationToken);
        var statusChangeCount = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM StatusChanges;",
            cancellationToken);
        var oldestTelemetry = await ReadNullableUnixTimestampAsync(
            connection,
            "SELECT MIN(CapturedAtUnixMs) FROM TelemetrySamples;",
            cancellationToken);
        var newestTelemetry = await ReadNullableUnixTimestampAsync(
            connection,
            "SELECT MAX(CapturedAtUnixMs) FROM TelemetrySamples;",
            cancellationToken);
        var lastMaintenanceText = await ReadMaintenanceValueAsync(
            connection,
            "LastMaintenanceAtUtc",
            cancellationToken);

        var drive = new DriveInfo(Path.GetPathRoot(_databasePath)!);
        return new HistorianStorageStatus(
            FileLength(_databasePath),
            FileLength($"{_databasePath}-wal"),
            FileLength($"{_databasePath}-shm"),
            reusablePages * pageSize,
            drive.AvailableFreeSpace,
            drive.TotalSize,
            telemetryCount,
            snapshotCount,
            statusChangeCount,
            oldestTelemetry,
            newestTelemetry,
            DateTimeOffset.TryParse(
                lastMaintenanceText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var lastMaintenance)
                    ? lastMaintenance
                    : null);
    }

    private static async Task<(int Count, long LastId)> BackfillLegacySnapshotsAsync(
        SqliteConnection connection,
        long lastBackfilledId,
        int batchSize,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<(long Id, DateTimeOffset At, string Payload, string MappingVersion)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, CapturedAtUtc, PayloadJson, MappingVersion
                FROM StatusSnapshots
                WHERE Id > @LastId
                ORDER BY Id
                LIMIT @BatchSize;
                """;
            command.Parameters.AddWithValue("@LastId", lastBackfilledId);
            command.Parameters.AddWithValue("@BatchSize", batchSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                snapshots.Add((
                    reader.GetInt64(0),
                    ParseDatabaseTimestamp(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        if (snapshots.Count == 0)
            return (0, lastBackfilledId);

        await using var transaction = connection.BeginTransaction();
        foreach (var snapshot in snapshots)
        {
            using var document = JsonDocument.Parse(snapshot.Payload);
            await UpsertMinuteAggregateAsync(
                connection,
                transaction,
                snapshot.At,
                TelemetryCatalog.Extract(document.RootElement),
                snapshot.MappingVersion,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        var lastId = snapshots[^1].Id;
        await WriteMaintenanceStateAsync(
            connection,
            "LegacyBackfillLastId",
            lastId.ToString(CultureInfo.InvariantCulture),
            nowUtc,
            cancellationToken);
        return (snapshots.Count, lastId);
    }

    private static async Task<bool> HasMatureOptimizedTelemetryAsync(
        SqliteConnection connection,
        DateTimeOffset maturityCutoffUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM TelemetrySamples
                WHERE CapturedAtUnixMs <= @CutoffUnixMs
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue(
            "@CutoffUnixMs",
            maturityCutoffUtc.ToUnixTimeMilliseconds());
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<int> DeleteBatchAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ReadMaintenanceLongAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        var value = await ReadMaintenanceValueAsync(connection, key, cancellationToken);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static async Task<string?> ReadMaintenanceValueAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT StateValue
            FROM HistorianMaintenanceState
            WHERE StateKey = @StateKey;
            """;
        command.Parameters.AddWithValue("@StateKey", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static Task WriteMaintenanceStateAsync(
        SqliteConnection connection,
        string key,
        string value,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            null,
            """
            INSERT INTO HistorianMaintenanceState
                (StateKey, StateValue, UpdatedAtUtc)
            VALUES
                (@StateKey, @StateValue, @UpdatedAtUtc)
            ON CONFLICT(StateKey) DO UPDATE SET
                StateValue = excluded.StateValue,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """,
            cancellationToken,
            ("@StateKey", key),
            ("@StateValue", value),
            ("@UpdatedAtUtc", ToDatabaseTimestamp(nowUtc)));

    private static async Task<DateTimeOffset?> ReadNullableUnixTimestampAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(
                Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static long FileLength(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }

    private static string ReadCatalogText(SqliteDataReader reader, int ordinal, string fallback) =>
        reader.IsDBNull(ordinal) || string.IsNullOrWhiteSpace(reader.GetString(ordinal))
            ? fallback
            : reader.GetString(ordinal);

    private async Task<IReadOnlyList<TelemetrySampleRow>> ReadOptimizedTelemetryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        bool useMinuteAggregates,
        CancellationToken cancellationToken)
    {
        var source = useMinuteAggregates
            ? "TelemetryMinuteAggregates"
            : "TelemetrySamples";
        var timestampColumn = useMinuteAggregates
            ? "BucketUnixMs"
            : "CapturedAtUnixMs";
        var selectedColumns = TelemetryCatalog.NumericFields
            .Concat(TelemetryCatalog.PaperPresenceFields)
            .Select(QuoteIdentifier);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {timestampColumn}, {string.Join(", ", selectedColumns)},
                   MappingVersion, Quality
            FROM {source}
            WHERE {timestampColumn} >= @FromUnixMs
              AND {timestampColumn} < @ToUnixMs
            ORDER BY {timestampColumn};
            """;
        command.Parameters.AddWithValue("@FromUnixMs", fromUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@ToUnixMs", toUtc.ToUnixTimeMilliseconds());

        var rows = new List<TelemetrySampleRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ordinal = 1;
            var numeric = new Dictionary<string, double?>(StringComparer.Ordinal);
            foreach (var field in TelemetryCatalog.NumericFields)
            {
                numeric[field] = reader.IsDBNull(ordinal)
                    ? null
                    : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
                ordinal++;
            }

            var boolean = new Dictionary<string, bool?>(StringComparer.Ordinal);
            foreach (var field in TelemetryCatalog.PaperPresenceFields)
            {
                if (reader.IsDBNull(ordinal))
                {
                    boolean[field] = null;
                }
                else
                {
                    var value = Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
                    boolean[field] = useMinuteAggregates ? value >= 0.5 : value != 0;
                }
                ordinal++;
            }

            rows.Add(new TelemetrySampleRow(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                numeric,
                boolean,
                reader.GetString(ordinal),
                reader.GetString(ordinal + 1)));
        }

        return rows;
    }

    private async Task<IReadOnlyList<MachineProductivitySampleRow>> ReadLegacyProductivityAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateRangeCommand(
            connection,
            """
            SELECT CapturedAtUtc,
                   json_extract(PayloadJson, '$.dryingSectionGroup3UpperMasterSpeedMPM'),
                   json_extract(PayloadJson, '$.dryingSectionGroup3PaperPresence'),
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

        var rows = new List<MachineProductivitySampleRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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

    private static IReadOnlyList<TelemetrySampleRow> Downsample(
        IReadOnlyList<TelemetrySampleRow> rows,
        int maximumPoints)
    {
        if (rows.Count <= maximumPoints)
            return rows;

        var sampled = new List<TelemetrySampleRow>(maximumPoints);
        for (var index = 0; index < maximumPoints; index++)
        {
            var sourceIndex = (int)Math.Round(
                index * (rows.Count - 1d) / (maximumPoints - 1d),
                MidpointRounding.AwayFromZero);
            sampled.Add(rows[sourceIndex]);
        }

        return sampled;
    }

    private static async Task EnsureOptimizedHistorianSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var numericColumns = string.Join(
            ",\n                ",
            TelemetryCatalog.NumericFields.Select(field => $"{QuoteIdentifier(field)} REAL NULL"));
        var booleanColumns = string.Join(
            ",\n                ",
            TelemetryCatalog.PaperPresenceFields.Select(field => $"{QuoteIdentifier(field)} INTEGER NULL"));
        var aggregateNumericColumns = string.Join(
            ",\n                ",
            TelemetryCatalog.NumericFields.Select(field => $"{QuoteIdentifier(field)} REAL NULL"));
        var aggregateBooleanColumns = string.Join(
            ",\n                ",
            TelemetryCatalog.PaperPresenceFields.Select(field => $"{QuoteIdentifier(field)} REAL NULL"));

        await ExecuteAsync(
            connection,
            null,
            $"""
            CREATE TABLE IF NOT EXISTS TelemetrySamples (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                CapturedAtUnixMs INTEGER NOT NULL,
                MappingVersion TEXT NOT NULL,
                Quality TEXT NOT NULL,
                {numericColumns},
                {booleanColumns}
            );
            CREATE INDEX IF NOT EXISTS IX_TelemetrySamples_CapturedAt
                ON TelemetrySamples (CapturedAtUnixMs);

            CREATE TABLE IF NOT EXISTS TelemetryMinuteAggregates (
                BucketUnixMs INTEGER NOT NULL PRIMARY KEY,
                SampleCount INTEGER NOT NULL,
                MappingVersion TEXT NOT NULL,
                Quality TEXT NOT NULL,
                {aggregateNumericColumns},
                {aggregateBooleanColumns},
                MachineSpeedMinimum REAL NULL,
                MachineSpeedMaximum REAL NULL
            );

            CREATE TABLE IF NOT EXISTS PaperBreakEvents (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                StartedAtUnixMs INTEGER NOT NULL,
                EndedAtUnixMs INTEGER NULL,
                DurationMilliseconds INTEGER NULL,
                ActiveAtStartup INTEGER NOT NULL,
                SpeedAtStartMpm REAL NOT NULL,
                SpeedAtEndMpm REAL NULL,
                MappingVersion TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PaperBreakEvents_StartedAt
                ON PaperBreakEvents (StartedAtUnixMs);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_PaperBreakEvents_Open
                ON PaperBreakEvents ((1)) WHERE EndedAtUnixMs IS NULL;

            CREATE TABLE IF NOT EXISTS HistorianMaintenanceState (
                StateKey TEXT NOT NULL PRIMARY KEY,
                StateValue TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_StatusChanges_ObservedAt
                ON StatusChanges (ObservedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_CommandEvents_ObservedAt
                ON CommandEvents (ObservedAtUtc);
            CREATE INDEX IF NOT EXISTS IX_AlarmEvents_ActivatedAt
                ON AlarmEvents (ActivatedAtUtc);
            """,
            cancellationToken);
    }

    private static async Task EnsurePaperBreakDiagnosticSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(PaperBreakEvents);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                existing.Add(reader.GetString(1));
        }

        var required = new (string Name, string Sql)[]
        {
            ("AnalysisStatus", "TEXT NOT NULL DEFAULT 'Pendente'"),
            ("CauseCategory", "TEXT NULL"),
            ("CauseDescription", "TEXT NULL"),
            ("AnalysisNotes", "TEXT NULL"),
            ("AnalyzedBy", "TEXT NULL"),
            ("AnalyzedAtUtc", "TEXT NULL")
        };
        foreach (var column in required)
        {
            if (existing.Contains(column.Name))
                continue;
            await ExecuteAsync(
                connection,
                null,
                $"ALTER TABLE PaperBreakEvents ADD COLUMN {column.Name} {column.Sql};",
                cancellationToken);
        }

        await ExecuteAsync(
            connection,
            null,
            """
            CREATE TABLE IF NOT EXISTS PaperBreakDiagnosticSamples (
                PaperBreakEventId INTEGER NOT NULL,
                CapturedAtUnixMs INTEGER NOT NULL,
                OffsetMilliseconds INTEGER NOT NULL,
                StatusJson TEXT NOT NULL,
                Quality TEXT NOT NULL,
                PRIMARY KEY (PaperBreakEventId, CapturedAtUnixMs),
                FOREIGN KEY (PaperBreakEventId) REFERENCES PaperBreakEvents(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_PaperBreakDiagnosticSamples_EventOffset
                ON PaperBreakDiagnosticSamples (PaperBreakEventId, OffsetMilliseconds);

            CREATE TABLE IF NOT EXISTS PaperBreakDiagnosticSummary (
                PaperBreakEventId INTEGER NOT NULL,
                FieldName TEXT NOT NULL,
                Category TEXT NOT NULL,
                Unit TEXT NOT NULL,
                SampleCount INTEGER NOT NULL,
                Minimum REAL NULL,
                Maximum REAL NULL,
                Average REAL NULL,
                StandardDeviation REAL NULL,
                ValueAtBreak REAL NULL,
                BaselineAverage REAL NULL,
                CriticalAverage REAL NULL,
                Delta REAL NULL,
                AnomalyScore REAL NULL,
                PRIMARY KEY (PaperBreakEventId, FieldName),
                FOREIGN KEY (PaperBreakEventId) REFERENCES PaperBreakEvents(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_PaperBreakDiagnosticSummary_Anomaly
                ON PaperBreakDiagnosticSummary (PaperBreakEventId, AnomalyScore DESC);

            CREATE TABLE IF NOT EXISTS PaperBreakEvidence (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                PaperBreakEventId INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                Name TEXT NOT NULL,
                PreviousValueJson TEXT NULL,
                CurrentValueJson TEXT NULL,
                ObservedAtUtc TEXT NOT NULL,
                OffsetMilliseconds INTEGER NOT NULL,
                Description TEXT NULL,
                Severity TEXT NULL,
                FOREIGN KEY (PaperBreakEventId) REFERENCES PaperBreakEvents(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_PaperBreakEvidence_EventOffset
                ON PaperBreakEvidence (PaperBreakEventId, OffsetMilliseconds);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_PaperBreakEvidence_EventOccurrence
                ON PaperBreakEvidence (PaperBreakEventId, Kind, Name, ObservedAtUtc);
            """,
            cancellationToken);
    }

    private static async Task InsertTelemetrySampleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset capturedAtUtc,
        TelemetryValues telemetry,
        string mappingVersion,
        CancellationToken cancellationToken)
    {
        var fields = TelemetryCatalog.NumericFields
            .Concat(TelemetryCatalog.PaperPresenceFields)
            .ToArray();
        var columns = string.Join(", ", fields.Select(QuoteIdentifier));
        var parameters = string.Join(", ", fields.Select((_, index) => $"@Value{index}"));
        var arguments = new List<(string Name, object? Value)>
        {
            ("@CapturedAtUnixMs", capturedAtUtc.ToUnixTimeMilliseconds()),
            ("@MappingVersion", mappingVersion)
        };

        for (var index = 0; index < TelemetryCatalog.NumericFields.Count; index++)
        {
            var field = TelemetryCatalog.NumericFields[index];
            arguments.Add(($"@Value{index}", telemetry.Numeric.GetValueOrDefault(field)));
        }

        for (var index = 0; index < TelemetryCatalog.PaperPresenceFields.Count; index++)
        {
            var field = TelemetryCatalog.PaperPresenceFields[index];
            var value = telemetry.Boolean.GetValueOrDefault(field);
            arguments.Add((
                $"@Value{TelemetryCatalog.NumericFields.Count + index}",
                value.HasValue ? (value.Value ? 1 : 0) : null));
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"""
            INSERT INTO TelemetrySamples
                (CapturedAtUnixMs, MappingVersion, Quality, {columns})
            VALUES
                (@CapturedAtUnixMs, @MappingVersion, 'Good', {parameters});
            """,
            cancellationToken,
            arguments.ToArray());

        await UpsertMinuteAggregateAsync(
            connection,
            transaction,
            capturedAtUtc,
            telemetry,
            mappingVersion,
            cancellationToken);
    }

    private static async Task UpsertMinuteAggregateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset capturedAtUtc,
        TelemetryValues telemetry,
        string mappingVersion,
        CancellationToken cancellationToken)
    {
        var fields = TelemetryCatalog.NumericFields
            .Concat(TelemetryCatalog.PaperPresenceFields)
            .ToArray();
        var columns = string.Join(", ", fields.Select(QuoteIdentifier));
        var parameters = string.Join(", ", fields.Select((_, index) => $"@AggregateValue{index}"));
        var updateAssignments = fields.Select(field =>
        {
            var identifier = QuoteIdentifier(field);
            return $"""
                {identifier} = CASE
                    WHEN excluded.{identifier} IS NULL THEN {identifier}
                    WHEN {identifier} IS NULL THEN excluded.{identifier}
                    ELSE (({identifier} * SampleCount) + excluded.{identifier}) / (SampleCount + 1)
                END
                """;
        });
        var machineSpeed = QuoteIdentifier(TelemetryCatalog.MachineSpeedField);
        var arguments = new List<(string Name, object? Value)>
        {
            ("@BucketUnixMs", capturedAtUtc.ToUnixTimeMilliseconds() / 60_000 * 60_000),
            ("@MappingVersion", mappingVersion)
        };

        for (var index = 0; index < TelemetryCatalog.NumericFields.Count; index++)
        {
            var field = TelemetryCatalog.NumericFields[index];
            arguments.Add(($"@AggregateValue{index}", telemetry.Numeric.GetValueOrDefault(field)));
        }

        for (var index = 0; index < TelemetryCatalog.PaperPresenceFields.Count; index++)
        {
            var field = TelemetryCatalog.PaperPresenceFields[index];
            var value = telemetry.Boolean.GetValueOrDefault(field);
            arguments.Add((
                $"@AggregateValue{TelemetryCatalog.NumericFields.Count + index}",
                value.HasValue ? (value.Value ? 1.0 : 0.0) : null));
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"""
            INSERT INTO TelemetryMinuteAggregates
                (BucketUnixMs, SampleCount, MappingVersion, Quality, {columns},
                 MachineSpeedMinimum, MachineSpeedMaximum)
            VALUES
                (@BucketUnixMs, 1, @MappingVersion, 'Good', {parameters},
                 @MachineSpeed, @MachineSpeed)
            ON CONFLICT(BucketUnixMs) DO UPDATE SET
                SampleCount = SampleCount + 1,
                MappingVersion = excluded.MappingVersion,
                Quality = excluded.Quality,
                {string.Join(",\n                ", updateAssignments)},
                MachineSpeedMinimum = CASE
                    WHEN excluded.MachineSpeedMinimum IS NULL THEN MachineSpeedMinimum
                    WHEN MachineSpeedMinimum IS NULL THEN excluded.MachineSpeedMinimum
                    ELSE MIN(MachineSpeedMinimum, excluded.MachineSpeedMinimum)
                END,
                MachineSpeedMaximum = CASE
                    WHEN excluded.MachineSpeedMaximum IS NULL THEN MachineSpeedMaximum
                    WHEN MachineSpeedMaximum IS NULL THEN excluded.MachineSpeedMaximum
                    ELSE MAX(MachineSpeedMaximum, excluded.MachineSpeedMaximum)
                END;
            """,
            cancellationToken,
            arguments
                .Append(("@MachineSpeed", telemetry.Numeric.GetValueOrDefault(
                    TelemetryCatalog.MachineSpeedField)))
                .ToArray());
    }

    private static async Task<long?> GetPaperBreakEventIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT Id FROM PaperBreakEvents WHERE StartedAtUnixMs = @StartedAtUnixMs LIMIT 1;";
        command.Parameters.AddWithValue(
            "@StartedAtUnixMs",
            startedAtUtc.ToUnixTimeMilliseconds());
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task InsertPaperBreakDiagnosticAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long paperBreakEventId,
        PaperBreakDiagnosticCapture diagnostic,
        CancellationToken cancellationToken)
    {
        foreach (var sample in diagnostic.Samples)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT OR IGNORE INTO PaperBreakDiagnosticSamples
                    (PaperBreakEventId, CapturedAtUnixMs, OffsetMilliseconds, StatusJson, Quality)
                VALUES
                    (@PaperBreakEventId, @CapturedAtUnixMs, @OffsetMilliseconds, @StatusJson, 'Good');
                """,
                cancellationToken,
                ("@PaperBreakEventId", paperBreakEventId),
                ("@CapturedAtUnixMs", sample.CapturedAtUtc.ToUnixTimeMilliseconds()),
                ("@OffsetMilliseconds",
                    sample.CapturedAtUtc.ToUnixTimeMilliseconds() -
                    diagnostic.BreakAtUtc.ToUnixTimeMilliseconds()),
                ("@StatusJson", SerializeDiagnosticStatus(sample.Status)));
        }

        foreach (var summary in BuildPaperBreakSummary(diagnostic))
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT OR REPLACE INTO PaperBreakDiagnosticSummary
                    (PaperBreakEventId, FieldName, Category, Unit, SampleCount,
                     Minimum, Maximum, Average, StandardDeviation, ValueAtBreak,
                     BaselineAverage, CriticalAverage, Delta, AnomalyScore)
                VALUES
                    (@PaperBreakEventId, @FieldName, @Category, @Unit, @SampleCount,
                     @Minimum, @Maximum, @Average, @StandardDeviation, @ValueAtBreak,
                     @BaselineAverage, @CriticalAverage, @Delta, @AnomalyScore);
                """,
                cancellationToken,
                ("@PaperBreakEventId", paperBreakEventId),
                ("@FieldName", summary.FieldName),
                ("@Category", summary.Category),
                ("@Unit", summary.Unit),
                ("@SampleCount", summary.SampleCount),
                ("@Minimum", summary.Minimum),
                ("@Maximum", summary.Maximum),
                ("@Average", summary.Average),
                ("@StandardDeviation", summary.StandardDeviation),
                ("@ValueAtBreak", summary.ValueAtBreak),
                ("@BaselineAverage", summary.BaselineAverage),
                ("@CriticalAverage", summary.CriticalAverage),
                ("@Delta", summary.Delta),
                ("@AnomalyScore", summary.AnomalyScore));
        }
    }

    private static IReadOnlyList<PaperBreakDiagnosticSummaryRow> BuildPaperBreakSummary(
        PaperBreakDiagnosticCapture diagnostic)
    {
        var values = new Dictionary<string, List<(long Offset, double Value)>>(
            StringComparer.OrdinalIgnoreCase);
        var breakAtUnixMs = diagnostic.BreakAtUtc.ToUnixTimeMilliseconds();
        foreach (var sample in diagnostic.Samples)
        {
            var offset = sample.CapturedAtUtc.ToUnixTimeMilliseconds() - breakAtUnixMs;
            foreach (var property in sample.Status.EnumerateObject())
            {
                if (!IsPaperBreakDiagnosticField(property.Name, property.Value) ||
                    property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetDouble(out var number) ||
                    !double.IsFinite(number))
                {
                    continue;
                }

                if (!values.TryGetValue(property.Name, out var fieldValues))
                {
                    fieldValues = [];
                    values[property.Name] = fieldValues;
                }
                fieldValues.Add((offset, number));
            }
        }

        return values.Select(item =>
        {
            var all = item.Value.Select(value => value.Value).ToArray();
            var average = all.Average();
            var variance = all.Select(value => Math.Pow(value - average, 2)).Average();
            var standardDeviation = Math.Sqrt(variance);
            var baseline = item.Value
                .Where(value => value.Offset <= -60_000)
                .Select(value => value.Value)
                .ToArray();
            var critical = item.Value
                .Where(value => value.Offset >= -30_000)
                .Select(value => value.Value)
                .ToArray();
            var baselineAverage = baseline.Length == 0 ? (double?)null : baseline.Average();
            var criticalAverage = critical.Length == 0 ? (double?)null : critical.Average();
            double? delta = baselineAverage.HasValue && criticalAverage.HasValue
                ? criticalAverage.Value - baselineAverage.Value
                : null;
            double? anomaly = delta.HasValue
                ? Math.Min(
                    99,
                    standardDeviation > 0.0001
                        ? Math.Abs(delta.Value) / standardDeviation
                        : Math.Abs(delta.Value) > 0.0001 ? 10 : 0)
                : null;
            var (category, unit) = DescribeDiagnosticField(item.Key);
            return new PaperBreakDiagnosticSummaryRow(
                item.Key,
                category,
                unit,
                all.Length,
                all.Min(),
                all.Max(),
                average,
                standardDeviation,
                item.Value.OrderBy(value => value.Offset).Last().Value,
                baselineAverage,
                criticalAverage,
                delta,
                anomaly);
        })
        .OrderByDescending(item => item.AnomalyScore ?? -1)
        .ThenBy(item => item.FieldName, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    private static string SerializeDiagnosticStatus(JsonElement status)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in status.EnumerateObject())
            {
                if (!IsPaperBreakDiagnosticField(property.Name, property.Value))
                    continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsPaperBreakDiagnosticField(string fieldName, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return true;
        if (value.ValueKind != JsonValueKind.Number)
            return false;

        return fieldName.Contains("Speed", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Torque", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Pressure", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("MMH2O", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Position", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Temperature", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Level", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Vacuum", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Flow", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Setpoint", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("CtrlOutput", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Ratio", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Current", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Voltage", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Frequency", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("Diameter", StringComparison.OrdinalIgnoreCase) ||
               fieldName.EndsWith("State", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("FaultCode", StringComparison.OrdinalIgnoreCase) ||
               fieldName.Contains("EventCounter", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Category, string Unit) DescribeDiagnosticField(string fieldName)
    {
        if (fieldName.Contains("Torque", StringComparison.OrdinalIgnoreCase))
            return ("Torque", "%");
        if (fieldName.Contains("Speed", StringComparison.OrdinalIgnoreCase))
        {
            var isPump = fieldName.Contains("Pump", StringComparison.OrdinalIgnoreCase);
            return (isPump ? "Bombas" : "Velocidade", isPump ? "%" : "m/min");
        }
        if (fieldName.Contains("Pressure", StringComparison.OrdinalIgnoreCase))
            return ("Pressão", "bar");
        if (fieldName.Contains("MMH2O", StringComparison.OrdinalIgnoreCase))
            return ("Headbox", "mmH₂O");
        if (fieldName.Contains("Position", StringComparison.OrdinalIgnoreCase))
            return ("Posição", "mm");
        if (fieldName.Contains("Temperature", StringComparison.OrdinalIgnoreCase))
            return ("Temperatura", "°C");
        if (fieldName.Contains("Level", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("CtrlOutput", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("Setpoint", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("Ratio", StringComparison.OrdinalIgnoreCase))
        {
            return ("Processo", "%");
        }
        if (fieldName.EndsWith("State", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("FaultCode", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("EventCounter", StringComparison.OrdinalIgnoreCase))
        {
            return ("Estado", "código");
        }
        return ("Processo", "unidade PLC");
    }

    private static async Task InsertPaperBreakEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long paperBreakEventId,
        DateTimeOffset breakAtUtc,
        CancellationToken cancellationToken)
    {
        var fromUtc = breakAtUtc.AddMinutes(-3);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO PaperBreakEvidence
                (PaperBreakEventId, Kind, Name, PreviousValueJson, CurrentValueJson,
                 ObservedAtUtc, OffsetMilliseconds, Description, Severity)
            SELECT @PaperBreakEventId, 'Comando', CommandName, PreviousValueJson, CurrentValueJson,
                   ObservedAtUtc,
                   CAST(ROUND((julianday(ObservedAtUtc) - julianday(@BreakAtUtc)) * 86400000) AS INTEGER),
                   Origin, NULL
            FROM CommandEvents
            WHERE ObservedAtUtc >= @FromUtc AND ObservedAtUtc <= @BreakAtUtc;

            INSERT OR IGNORE INTO PaperBreakEvidence
                (PaperBreakEventId, Kind, Name, PreviousValueJson, CurrentValueJson,
                 ObservedAtUtc, OffsetMilliseconds, Description, Severity)
            SELECT @PaperBreakEventId, 'Status', FieldName, PreviousValueJson, CurrentValueJson,
                   ObservedAtUtc,
                   CAST(ROUND((julianday(ObservedAtUtc) - julianday(@BreakAtUtc)) * 86400000) AS INTEGER),
                   NULL, NULL
            FROM StatusChanges
            WHERE ObservedAtUtc >= @FromUtc AND ObservedAtUtc <= @BreakAtUtc;

            INSERT OR IGNORE INTO PaperBreakEvidence
                (PaperBreakEventId, Kind, Name, PreviousValueJson, CurrentValueJson,
                 ObservedAtUtc, OffsetMilliseconds, Description, Severity)
            SELECT @PaperBreakEventId, 'Alarme', AlarmName, 'false', 'true',
                   ActivatedAtUtc,
                   CAST(ROUND((julianday(ActivatedAtUtc) - julianday(@BreakAtUtc)) * 86400000) AS INTEGER),
                   DisplayName || CASE WHEN Description = '' THEN '' ELSE ' — ' || Description END,
                   Severity
            FROM AlarmEvents
            WHERE ActivatedAtUtc >= @FromUtc AND ActivatedAtUtc <= @BreakAtUtc;

            INSERT OR IGNORE INTO PaperBreakEvidence
                (PaperBreakEventId, Kind, Name, PreviousValueJson, CurrentValueJson,
                 ObservedAtUtc, OffsetMilliseconds, Description, Severity)
            SELECT @PaperBreakEventId, 'Alarme ativo', AlarmName, NULL, 'true',
                   @BreakAtUtc, 0,
                   DisplayName || ' — já estava ativo no início da janela',
                   Severity
            FROM AlarmEvents
            WHERE ActivatedAtUtc < @FromUtc
              AND (ClearedAtUtc IS NULL OR ClearedAtUtc > @BreakAtUtc);
            """,
            cancellationToken,
            ("@PaperBreakEventId", paperBreakEventId),
            ("@FromUtc", ToDatabaseTimestamp(fromUtc)),
            ("@BreakAtUtc", ToDatabaseTimestamp(breakAtUtc)));
    }

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

    private static void AddHistorySearchParameters(
        SqliteCommand command,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? search)
    {
        command.Parameters.AddWithValue("@FromUtc", ToDatabaseTimestamp(fromUtc));
        command.Parameters.AddWithValue("@ToUtc", ToDatabaseTimestamp(toUtc));
        command.Parameters.AddWithValue(
            "@Search",
            BuildLikePattern(search) is { } pattern ? pattern : DBNull.Value);
    }

    private static void AddPaperBreakSearchParameters(
        SqliteCommand command,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? analysisStatus,
        string? causeCategory,
        string? search)
    {
        command.Parameters.AddWithValue("@FromUnixMs", fromUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@ToUnixMs", toUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue(
            "@AnalysisStatus",
            string.IsNullOrWhiteSpace(analysisStatus)
                ? DBNull.Value
                : analysisStatus.Trim());
        command.Parameters.AddWithValue(
            "@CauseCategory",
            string.IsNullOrWhiteSpace(causeCategory)
                ? DBNull.Value
                : causeCategory.Trim());
        command.Parameters.AddWithValue(
            "@Search",
            BuildLikePattern(search) is { } pattern ? pattern : DBNull.Value);
    }

    private static string? BuildLikePattern(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return null;
        var escaped = search.Trim()
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
        return $"%{escaped}%";
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

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static DateTimeOffset ParseDatabaseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static double? ReadNullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static PaperBreakEventRow ReadPaperBreakEvent(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            reader.IsDBNull(2)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.GetInt64(4) != 0,
            reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : ParseDatabaseTimestamp(reader.GetString(14)));

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 5_000)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 5000.");
    }

    private static void ValidateHistoryPage(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int offset,
        int limit)
    {
        if (fromUtc >= toUtc)
            throw new ArgumentException("History start must be before its end.");
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit), "Page size must be between 1 and 500.");
    }
}
