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
    Task<IReadOnlyList<CommandEventRow>> GetCommandEventsAsync(
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

public sealed record AlarmEventRow(
    long Id,
    string AlarmName,
    DateTimeOffset ActivatedAtUtc,
    DateTimeOffset? ClearedAtUtc,
    long? DurationMilliseconds,
    bool ActiveAtStartup,
    string MappingVersion);
