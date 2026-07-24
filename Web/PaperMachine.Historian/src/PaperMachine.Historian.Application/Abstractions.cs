using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Application;

public interface IPaperMachineReader : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task<PaperMachineSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

public interface IHistorianRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task PersistCycleAsync(HistorianCycle cycle, CancellationToken cancellationToken);
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
}

public interface IUserRepository
{
    Task<int> CountAsync(CancellationToken cancellationToken);
    Task<ApplicationUser?> FindByUserNameAsync(string userName, CancellationToken cancellationToken);
    Task<ApplicationUser?> FindByIdAsync(long id, CancellationToken cancellationToken);
    Task<long> CreateAsync(
        string userName,
        string displayName,
        string passwordHash,
        string role,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken);
    Task MarkLoginAsync(long id, DateTimeOffset loggedInAtUtc, CancellationToken cancellationToken);
}

public sealed record StatusSnapshotRow(
    long Id,
    DateTimeOffset CapturedAtUtc,
    string PayloadJson,
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
