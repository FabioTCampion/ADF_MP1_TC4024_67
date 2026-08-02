using System.Globalization;
using Microsoft.Data.Sqlite;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Infrastructure.Database;

public sealed class SqliteProductionIntegrationRepository : IProductionIntegrationRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SqliteProductionIntegrationRepository(DatabaseOptions options)
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

    public async Task ApplyObservationAsync(
        ProductionSourceObservation observation,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var observedAt = Timestamp(observation.ObservedAtUtc);
            var previousHash = await ScalarStringAsync(
                connection,
                transaction,
                "SELECT LastPayloadHash FROM IntegrationSyncState WHERE IntegrationKey = @Source;",
                cancellationToken,
                ("@Source", observation.SourceSystem));

            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE ExternalProductionRuns
                SET IsProducing = 0,
                    ClosedAtUtc = COALESCE(ClosedAtUtc, @ObservedAtUtc)
                WHERE SourceSystem = @SourceSystem
                  AND ExternalRunId <> @ExternalRunId
                  AND ClosedAtUtc IS NULL;

                UPDATE ProductionQualityPeriods
                SET EndedAtUtc = @ObservedAtUtc
                WHERE SourceSystem = @SourceSystem
                  AND EndedAtUtc IS NULL
                  AND RunId IN (
                      SELECT Id FROM ExternalProductionRuns
                      WHERE SourceSystem = @SourceSystem
                        AND ExternalRunId <> @ExternalRunId
                  );
                """,
                cancellationToken,
                ("@ObservedAtUtc", observedAt),
                ("@SourceSystem", observation.SourceSystem),
                ("@ExternalRunId", observation.ExternalRunId));

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO ExternalProductionRuns (
                    SourceSystem, ExternalRunId, ProductionOrderCode, MachineCode,
                    IsProducing, ExpectedEndAtUtc, FirstObservedAtUtc, LastObservedAtUtc,
                    ClosedAtUtc, QualityKey, QualityProductCode, QualityGrammageGsm,
                    ProductionWidthMm, IsMixedQuality)
                VALUES (
                    @SourceSystem, @ExternalRunId, @ProductionOrderCode, @MachineCode,
                    @IsProducing, @ExpectedEndAtUtc, @ObservedAtUtc, @ObservedAtUtc,
                    @ClosedAtUtc, @QualityKey, @QualityProductCode, @QualityGrammageGsm,
                    @ProductionWidthMm, @IsMixedQuality)
                ON CONFLICT (SourceSystem, ExternalRunId) DO UPDATE SET
                    ProductionOrderCode = excluded.ProductionOrderCode,
                    MachineCode = excluded.MachineCode,
                    IsProducing = excluded.IsProducing,
                    ExpectedEndAtUtc = excluded.ExpectedEndAtUtc,
                    LastObservedAtUtc = excluded.LastObservedAtUtc,
                    ClosedAtUtc = excluded.ClosedAtUtc,
                    QualityKey = excluded.QualityKey,
                    QualityProductCode = excluded.QualityProductCode,
                    QualityGrammageGsm = excluded.QualityGrammageGsm,
                    ProductionWidthMm = excluded.ProductionWidthMm,
                    IsMixedQuality = excluded.IsMixedQuality;
                """,
                cancellationToken,
                ("@SourceSystem", observation.SourceSystem),
                ("@ExternalRunId", observation.ExternalRunId),
                ("@ProductionOrderCode", Db(observation.ProductionOrderCode)),
                ("@MachineCode", Db(observation.MachineCode)),
                ("@IsProducing", observation.IsProducing ? 1 : 0),
                ("@ExpectedEndAtUtc", Db(observation.ExpectedEndAtUtc is null
                    ? null
                    : Timestamp(observation.ExpectedEndAtUtc.Value))),
                ("@ObservedAtUtc", observedAt),
                ("@ClosedAtUtc", observation.IsProducing ? DBNull.Value : observedAt),
                ("@QualityKey", Db(observation.QualityKey)),
                ("@QualityProductCode", Db(observation.QualityProductCode)),
                ("@QualityGrammageGsm", Db(observation.QualityGrammageGsm)),
                ("@ProductionWidthMm", Db(observation.ProductionWidthMm)),
                ("@IsMixedQuality", observation.IsMixedQuality ? 1 : 0));

            var runId = await ScalarInt64Async(
                connection,
                transaction,
                """
                SELECT Id FROM ExternalProductionRuns
                WHERE SourceSystem = @SourceSystem AND ExternalRunId = @ExternalRunId;
                """,
                cancellationToken,
                ("@SourceSystem", observation.SourceSystem),
                ("@ExternalRunId", observation.ExternalRunId));

            if (!string.Equals(previousHash, observation.PayloadHash, StringComparison.Ordinal))
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    DELETE FROM ExternalProductionRunItems WHERE RunId = @RunId;
                    DELETE FROM ExternalProductionReferences WHERE RunId = @RunId;
                    INSERT INTO ExternalProductionSnapshots
                        (RunId, CapturedAtUtc, PayloadHash, RawPayloadJson)
                    VALUES
                        (@RunId, @CapturedAtUtc, @PayloadHash, @RawPayloadJson);
                    """,
                    cancellationToken,
                    ("@RunId", runId),
                    ("@CapturedAtUtc", observedAt),
                    ("@PayloadHash", observation.PayloadHash),
                    ("@RawPayloadJson", observation.RawPayloadJson));

                foreach (var item in observation.Items)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO ExternalProductionRunItems (
                            RunId, Position, CustomerName, OrderCode, ProductCode,
                            Format, Diameter, GrammageGsm, PlannedQuantityKg,
                            ProducedQuantityKg)
                        VALUES (
                            @RunId, @Position, @CustomerName, @OrderCode, @ProductCode,
                            @Format, @Diameter, @GrammageGsm, @PlannedQuantityKg,
                            @ProducedQuantityKg);
                        """,
                        cancellationToken,
                        ("@RunId", runId),
                        ("@Position", item.Position),
                        ("@CustomerName", Db(item.CustomerName)),
                        ("@OrderCode", Db(item.OrderCode)),
                        ("@ProductCode", Db(item.ProductCode)),
                        ("@Format", Db(item.Format)),
                        ("@Diameter", Db(item.Diameter)),
                        ("@GrammageGsm", Db(item.GrammageGsm)),
                        ("@PlannedQuantityKg", Db(item.PlannedQuantityKg)),
                        ("@ProducedQuantityKg", Db(item.ProducedQuantityKg)));
                }

                foreach (var reference in observation.References)
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO ExternalProductionReferences
                            (RunId, Position, ReferenceType, ReferenceValue)
                        VALUES
                            (@RunId, @Position, @ReferenceType, @ReferenceValue);
                        """,
                        cancellationToken,
                        ("@RunId", runId),
                        ("@Position", reference.Position),
                        ("@ReferenceType", reference.ReferenceType),
                        ("@ReferenceValue", reference.ReferenceValue));
                }
            }

            await UpdateQualityPeriodAsync(
                connection,
                transaction,
                runId,
                observation,
                observedAt,
                cancellationToken);

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO IntegrationSyncState (
                    IntegrationKey, LastAttemptAtUtc, LastSuccessfulSyncAtUtc,
                    Status, LastError, LastPayloadHash)
                VALUES (@SourceSystem, @ObservedAtUtc, @ObservedAtUtc, 'Online', NULL, @PayloadHash)
                ON CONFLICT (IntegrationKey) DO UPDATE SET
                    LastAttemptAtUtc = excluded.LastAttemptAtUtc,
                    LastSuccessfulSyncAtUtc = excluded.LastSuccessfulSyncAtUtc,
                    Status = excluded.Status,
                    LastError = NULL,
                    LastPayloadHash = excluded.LastPayloadHash;
                """,
                cancellationToken,
                ("@SourceSystem", observation.SourceSystem),
                ("@ObservedAtUtc", observedAt),
                ("@PayloadHash", observation.PayloadHash));

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RecordFailureAsync(
        string sourceSystem,
        DateTimeOffset attemptedAtUtc,
        string sanitizedError,
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
                INSERT INTO IntegrationSyncState (
                    IntegrationKey, LastAttemptAtUtc, LastSuccessfulSyncAtUtc,
                    Status, LastError, LastPayloadHash)
                VALUES (@SourceSystem, @AttemptedAtUtc, NULL, 'Faulted', @LastError, NULL)
                ON CONFLICT (IntegrationKey) DO UPDATE SET
                    LastAttemptAtUtc = excluded.LastAttemptAtUtc,
                    Status = excluded.Status,
                    LastError = excluded.LastError;
                """,
                cancellationToken,
                ("@SourceSystem", sourceSystem),
                ("@AttemptedAtUtc", Timestamp(attemptedAtUtc)),
                ("@LastError", sanitizedError.Length <= 500
                    ? sanitizedError
                    : sanitizedError[..500]));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<ProductionIntegrationState> GetStateAsync(
        string sourceSystem,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        DateTimeOffset? lastAttempt = null;
        DateTimeOffset? lastSuccess = null;
        var status = "NeverSynced";
        string? lastError = null;
        string? lastHash = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT LastAttemptAtUtc, LastSuccessfulSyncAtUtc, Status, LastError, LastPayloadHash
                FROM IntegrationSyncState WHERE IntegrationKey = @SourceSystem;
                """;
            command.Parameters.AddWithValue("@SourceSystem", sourceSystem);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                lastAttempt = ReadTimestamp(reader, 0);
                lastSuccess = ReadTimestamp(reader, 1);
                status = reader.GetString(2);
                lastError = reader.IsDBNull(3) ? null : reader.GetString(3);
                lastHash = reader.IsDBNull(4) ? null : reader.GetString(4);
            }
        }

        ExternalProductionRunState? run = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, SourceSystem, ExternalRunId, ProductionOrderCode, MachineCode,
                       IsProducing, ExpectedEndAtUtc, FirstObservedAtUtc, LastObservedAtUtc,
                       ClosedAtUtc, QualityKey, QualityProductCode, QualityGrammageGsm,
                       ProductionWidthMm, IsMixedQuality
                FROM ExternalProductionRuns
                WHERE SourceSystem = @SourceSystem
                ORDER BY LastObservedAtUtc DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@SourceSystem", sourceSystem);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var runId = reader.GetInt64(0);
                run = new ExternalProductionRunState(
                    runId,
                    reader.GetString(1),
                    reader.GetString(2),
                    ReadString(reader, 3),
                    ReadString(reader, 4),
                    reader.GetInt64(5) != 0,
                    ReadTimestamp(reader, 6),
                    ReadTimestamp(reader, 7)!.Value,
                    ReadTimestamp(reader, 8)!.Value,
                    ReadTimestamp(reader, 9),
                    ReadString(reader, 10),
                    ReadString(reader, 11),
                    ReadDecimal(reader, 12),
                    ReadDecimal(reader, 13),
                    reader.GetInt64(14) != 0,
                    [],
                    []);
            }
        }
        if (run is not null)
        {
            run = run with
            {
                Items = await ReadItemsAsync(connection, run.Id, cancellationToken),
                References = await ReadReferencesAsync(connection, run.Id, cancellationToken)
            };
        }

        return new ProductionIntegrationState(
            sourceSystem,
            lastAttempt,
            lastSuccess,
            status,
            lastError,
            lastHash,
            run);
    }

    private static async Task UpdateQualityPeriodAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        ProductionSourceObservation observation,
        string observedAt,
        CancellationToken cancellationToken)
    {
        long? openId = null;
        long? openRunId = null;
        string? openQualityKey = null;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT Id, RunId, QualityKey FROM ProductionQualityPeriods
                WHERE SourceSystem = @SourceSystem AND EndedAtUtc IS NULL;
                """;
            command.Parameters.AddWithValue("@SourceSystem", observation.SourceSystem);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                openId = reader.GetInt64(0);
                openRunId = reader.GetInt64(1);
                openQualityKey = reader.GetString(2);
            }
        }

        var keepOpen = observation.IsProducing &&
                       !string.IsNullOrWhiteSpace(observation.QualityKey) &&
                       openRunId == runId &&
                       string.Equals(openQualityKey, observation.QualityKey, StringComparison.Ordinal);
        if (openId.HasValue && !keepOpen)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE ProductionQualityPeriods SET EndedAtUtc = @EndedAtUtc WHERE Id = @Id;",
                cancellationToken,
                ("@EndedAtUtc", observedAt),
                ("@Id", openId.Value));
        }

        if (observation.IsProducing &&
            !string.IsNullOrWhiteSpace(observation.QualityKey) &&
            !keepOpen)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO ProductionQualityPeriods (
                    SourceSystem, RunId, QualityKey, ProductCode, GrammageGsm, ProductionWidthMm,
                    IsMixedQuality, StartedAtUtc, EndedAtUtc)
                VALUES (
                    @SourceSystem, @RunId, @QualityKey, @ProductCode, @GrammageGsm, @ProductionWidthMm,
                    @IsMixedQuality, @StartedAtUtc, NULL);
                """,
                cancellationToken,
                ("@SourceSystem", observation.SourceSystem),
                ("@RunId", runId),
                ("@QualityKey", observation.QualityKey),
                ("@ProductCode", Db(observation.QualityProductCode)),
                ("@GrammageGsm", Db(observation.QualityGrammageGsm)),
                ("@ProductionWidthMm", Db(observation.ProductionWidthMm)),
                ("@IsMixedQuality", observation.IsMixedQuality ? 1 : 0),
                ("@StartedAtUtc", observedAt));
        }
    }

    private static async Task<IReadOnlyList<ExternalProductionItem>> ReadItemsAsync(
        SqliteConnection connection,
        long runId,
        CancellationToken cancellationToken)
    {
        var items = new List<ExternalProductionItem>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Position, CustomerName, OrderCode, ProductCode, Format, Diameter,
                   GrammageGsm, PlannedQuantityKg, ProducedQuantityKg
            FROM ExternalProductionRunItems WHERE RunId = @RunId ORDER BY Position;
            """;
        command.Parameters.AddWithValue("@RunId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ExternalProductionItem(
                reader.GetInt32(0), ReadString(reader, 1), ReadString(reader, 2),
                ReadString(reader, 3), ReadDecimal(reader, 4), ReadDecimal(reader, 5),
                ReadDecimal(reader, 6), ReadDecimal(reader, 7), ReadDecimal(reader, 8)));
        }
        return items;
    }

    private static async Task<IReadOnlyList<ExternalProductionReference>> ReadReferencesAsync(
        SqliteConnection connection,
        long runId,
        CancellationToken cancellationToken)
    {
        var references = new List<ExternalProductionReference>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Position, ReferenceType, ReferenceValue
            FROM ExternalProductionReferences WHERE RunId = @RunId ORDER BY Position;
            """;
        command.Parameters.AddWithValue("@RunId", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            references.Add(new ExternalProductionReference(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }
        return references;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA busy_timeout=5000;", cancellationToken);
        return connection;
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
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static object Db(object? value) => value ?? DBNull.Value;
    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
    private static string? ReadString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static decimal? ReadDecimal(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToDecimal(reader.GetDouble(ordinal), CultureInfo.InvariantCulture);
}
