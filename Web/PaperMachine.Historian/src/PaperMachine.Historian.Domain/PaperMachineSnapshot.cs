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
    bool InitialObservation,
    AlarmDefinition Definition,
    DriveFaultContext? DriveFault);

public sealed record PaperBreakTransition(
    bool IsActive,
    DateTimeOffset ObservedAtUtc,
    bool InitialObservation,
    double SpeedMpm);

public sealed record PaperBreakDiagnosticSample(
    DateTimeOffset CapturedAtUtc,
    JsonElement Status);

public sealed record PaperBreakDiagnosticCapture(
    DateTimeOffset BreakAtUtc,
    IReadOnlyList<PaperBreakDiagnosticSample> Samples);

public sealed record AlarmDefinition(
    string DisplayName,
    string Description,
    string RecommendedAction,
    string Severity,
    string Area,
    string CatalogVersion);

public sealed record DriveFaultContext(
    string Model,
    ushort Code,
    string CodeHex,
    string? Mnemonic,
    string Title,
    string Description,
    string RecommendedAction,
    double TorqueAtTrip,
    uint EventCounter,
    string ManualReference);

public sealed record HistorianCycle(
    PaperMachineSnapshot Snapshot,
    bool SaveTelemetrySample,
    bool SaveStatusSnapshot,
    IReadOnlyList<FieldChange> StatusChanges,
    IReadOnlyList<FieldChange> CommandChanges,
    IReadOnlyList<AlarmTransition> AlarmTransitions,
    IReadOnlyList<PaperBreakTransition> PaperBreakTransitions,
    IReadOnlyList<PaperBreakDiagnosticCapture> PaperBreakDiagnostics);

public sealed record CommunicationEvent(
    DateTimeOffset ObservedAtUtc,
    string State,
    string? Detail,
    string AmsNetId,
    int AdsPort);
