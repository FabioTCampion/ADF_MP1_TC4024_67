using System.Text.Json;

namespace PaperMachine.Historian.Domain;

public sealed record PaperMachineSnapshot(
    DateTimeOffset CapturedAtUtc,
    JsonElement Status,
    JsonElement Commands,
    JsonElement Alarms,
    string MappingVersion);

public sealed record FieldChange(
    string FieldName,
    string? PreviousValueJson,
    string CurrentValueJson,
    DateTimeOffset ObservedAtUtc);

public sealed record AlarmTransition(
    string AlarmName,
    bool IsActive,
    DateTimeOffset ObservedAtUtc,
    bool InitialObservation);

public sealed record HistorianCycle(
    PaperMachineSnapshot Snapshot,
    bool SaveStatusSnapshot,
    IReadOnlyList<FieldChange> StatusChanges,
    IReadOnlyList<FieldChange> CommandChanges,
    IReadOnlyList<AlarmTransition> AlarmTransitions);

public sealed record CommunicationEvent(
    DateTimeOffset ObservedAtUtc,
    string State,
    string? Detail,
    string AmsNetId,
    int AdsPort);
