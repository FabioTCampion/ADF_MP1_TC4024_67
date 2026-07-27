using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Application;

public interface IPaperMachineReader : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task<PaperMachineSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<FieldChange> ReadCommandChangesAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

public interface IHistorianRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task PersistCycleAsync(HistorianCycle cycle, CancellationToken cancellationToken);
    Task AddCommandEventAsync(
        FieldChange change,
        string mappingVersion,
        CancellationToken cancellationToken);
    Task AddCommunicationEventAsync(CommunicationEvent communicationEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<StatusSnapshotRow>> GetStatusSnapshotsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<StatusSnapshotRow>> GetStatusTrendSamplesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maximumPoints,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<TelemetrySampleRow>> GetTelemetryTrendSamplesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int maximumPoints,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<MachineProductivitySampleRow>> GetMachineProductivitySamplesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<CommandEventRow>> GetCommandEventsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken cancellationToken);
    Task<HistoryPage<CommandEventRow>> SearchCommandEventsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? search,
        int offset,
        int limit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<StatusChangeRow>> GetStatusChangesAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken cancellationToken);
    Task<HistoryPage<StatusChangeRow>> SearchStatusChangesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? search,
        int offset,
        int limit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<AlarmEventRow>> GetAlarmEventsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        bool? active,
        int limit,
        CancellationToken cancellationToken);
    Task<HistoryPage<AlarmEventRow>> SearchAlarmEventsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        bool? active,
        string? search,
        int offset,
        int limit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PaperBreakEventRow>> GetPaperBreakEventsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken cancellationToken);
    Task<HistoryPage<PaperBreakEventRow>> SearchPaperBreakEventsAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string? analysisStatus,
        string? causeCategory,
        string? search,
        int offset,
        int limit,
        CancellationToken cancellationToken);
    Task<PaperBreakDiagnosticRow?> GetPaperBreakDiagnosticAsync(
        long paperBreakEventId,
        CancellationToken cancellationToken);
    Task<bool> UpdatePaperBreakAnalysisAsync(
        long paperBreakEventId,
        PaperBreakAnalysisUpdate update,
        string analyzedBy,
        DateTimeOffset analyzedAtUtc,
        CancellationToken cancellationToken);
    Task<HistorianMaintenanceResult> RunMaintenanceAsync(
        DateTimeOffset nowUtc,
        HistorianOptions options,
        CancellationToken cancellationToken);
    Task<HistorianStorageStatus> GetStorageStatusAsync(CancellationToken cancellationToken);
}

public interface IUserRepository
{
    Task<int> CountAsync(CancellationToken cancellationToken);
    Task<int> CountActiveAdministratorsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ApplicationUser>> ListAsync(CancellationToken cancellationToken);
    Task<ApplicationUser?> FindByUserNameAsync(string userName, CancellationToken cancellationToken);
    Task<ApplicationUser?> FindByIdAsync(long id, CancellationToken cancellationToken);
    Task<long> CreateAsync(
        string userName,
        string displayName,
        string passwordHash,
        string role,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken);
    Task<bool> UpdateAsync(
        long id,
        string displayName,
        string role,
        bool isActive,
        CancellationToken cancellationToken);
    Task<bool> UpdatePasswordHashAsync(
        long id,
        string passwordHash,
        CancellationToken cancellationToken);
    Task MarkLoginAsync(long id, DateTimeOffset loggedInAtUtc, CancellationToken cancellationToken);
}

public interface IUserBreakAnalysisFilterRepository
{
    Task<IReadOnlyList<UserBreakAnalysisFilter>> ListAsync(
        long userId,
        CancellationToken cancellationToken);
    Task<UserBreakAnalysisFilterWriteResult> CreateAsync(
        long userId,
        string name,
        IReadOnlyList<string> variables,
        bool isDefault,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken);
    Task<UserBreakAnalysisFilterWriteResult> UpdateAsync(
        long id,
        long userId,
        string name,
        IReadOnlyList<string> variables,
        bool isDefault,
        int expectedRevision,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken);
    Task<UserBreakAnalysisFilterWriteStatus> DeleteAsync(
        long id,
        long userId,
        int expectedRevision,
        CancellationToken cancellationToken);
}

public sealed record StatusSnapshotRow(
    long Id,
    DateTimeOffset CapturedAtUtc,
    string PayloadJson,
    string MappingVersion,
    string Quality);

public sealed record MachineProductivitySampleRow(
    DateTimeOffset CapturedAtUtc,
    double? SpeedMpm,
    bool? PaperPresent,
    string Quality);

public sealed record TelemetrySampleRow(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyDictionary<string, double?> NumericValues,
    IReadOnlyDictionary<string, bool?> BooleanValues,
    string MappingVersion,
    string Quality);

public sealed record HistoryPage<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Offset,
    int Limit)
{
    public bool HasMore => Offset + Items.Count < Total;
}

public sealed record CommandEventRow(
    long Id,
    string CommandName,
    string? PreviousValueJson,
    string CurrentValueJson,
    DateTimeOffset ObservedAtUtc,
    string Origin,
    string MappingVersion);

public sealed record StatusChangeRow(
    long Id,
    string FieldName,
    string? PreviousValueJson,
    string CurrentValueJson,
    DateTimeOffset ObservedAtUtc,
    string MappingVersion);

public sealed record AlarmEventRow(
    long Id,
    string AlarmName,
    string DisplayName,
    string Description,
    string RecommendedAction,
    string Severity,
    string Area,
    string CatalogVersion,
    DateTimeOffset ActivatedAtUtc,
    DateTimeOffset? ClearedAtUtc,
    long? DurationMilliseconds,
    bool ActiveAtStartup,
    string MappingVersion,
    string? DriveModel,
    int? DriveFaultCode,
    string? DriveFaultCodeHex,
    string? DriveFaultMnemonic,
    string? DriveFaultTitle,
    string? DriveFaultDescription,
    string? DriveRecommendedAction,
    double? DriveFaultTorque,
    long? DriveFaultEventCounter,
    string? ManualReference);

public sealed record PaperBreakEventRow(
    long Id,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    long? DurationMilliseconds,
    bool ActiveAtStartup,
    double SpeedAtStartMpm,
    double? SpeedAtEndMpm,
    string MappingVersion,
    int DiagnosticSampleCount,
    string AnalysisStatus,
    string? CauseCategory,
    string? CauseDescription,
    string? AnalysisNotes,
    string? AnalyzedBy,
    DateTimeOffset? AnalyzedAtUtc);

public sealed record PaperBreakDiagnosticSampleRow(
    DateTimeOffset CapturedAtUtc,
    long OffsetMilliseconds,
    string StatusJson,
    string Quality);

public sealed record PaperBreakDiagnosticSummaryRow(
    string FieldName,
    string Category,
    string Unit,
    int SampleCount,
    double? Minimum,
    double? Maximum,
    double? Average,
    double? StandardDeviation,
    double? ValueAtBreak,
    double? BaselineAverage,
    double? CriticalAverage,
    double? Delta,
    double? AnomalyScore);

public sealed record PaperBreakEvidenceRow(
    long Id,
    string Kind,
    string Name,
    string? PreviousValueJson,
    string? CurrentValueJson,
    DateTimeOffset ObservedAtUtc,
    long OffsetMilliseconds,
    string? Description,
    string? Severity);

public sealed record PaperBreakDiagnosticRow(
    PaperBreakEventRow Event,
    IReadOnlyList<PaperBreakDiagnosticSampleRow> Samples,
    IReadOnlyList<PaperBreakDiagnosticSummaryRow> Summary,
    IReadOnlyList<PaperBreakEvidenceRow> Evidence);

public sealed record PaperBreakAnalysisUpdate(
    string AnalysisStatus,
    string? CauseCategory,
    string? CauseDescription,
    string? AnalysisNotes);

public sealed record HistorianMaintenanceResult(
    int DeletedDiagnosticSnapshots,
    int DeletedRawTelemetrySamples,
    int DeletedMinuteAggregates,
    int DeletedStatusChanges,
    int DeletedAnalogStatusChanges,
    int DeletedCommunicationEvents,
    DateTimeOffset CompletedAtUtc);

public sealed record HistorianStorageStatus(
    long DatabaseBytes,
    long WalBytes,
    long SharedMemoryBytes,
    long ReusableBytes,
    long FreeDiskBytes,
    long TotalDiskBytes,
    long TelemetrySampleCount,
    long DiagnosticSnapshotCount,
    long StatusChangeCount,
    DateTimeOffset? OldestTelemetryAtUtc,
    DateTimeOffset? NewestTelemetryAtUtc,
    DateTimeOffset? LastMaintenanceAtUtc);
