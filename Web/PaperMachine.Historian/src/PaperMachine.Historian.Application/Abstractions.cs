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
    Task<IReadOnlyList<StatusChangeRow>> GetStatusChangesAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<AlarmEventRow>> GetAlarmEventsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        bool? active,
        int limit,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PaperBreakEventRow>> GetPaperBreakEventsAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
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
    string MappingVersion);

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
