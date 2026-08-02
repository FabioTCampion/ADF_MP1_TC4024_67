namespace PaperMachine.Historian.Domain;

public sealed record ExternalProductionItem(
    int Position,
    string? CustomerName,
    string? OrderCode,
    string? ProductCode,
    decimal? Format,
    decimal? Diameter,
    decimal? GrammageGsm,
    decimal? PlannedQuantityKg,
    decimal? ProducedQuantityKg);

public sealed record ExternalProductionReference(
    int Position,
    string ReferenceType,
    string ReferenceValue);

public sealed record ProductionSourceObservation(
    string SourceSystem,
    string ExternalRunId,
    string? ProductionOrderCode,
    string? MachineCode,
    bool IsProducing,
    DateTimeOffset? ExpectedEndAtUtc,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<ExternalProductionItem> Items,
    IReadOnlyList<ExternalProductionReference> References,
    string RawPayloadJson,
    string PayloadHash,
    string? QualityKey,
    string? QualityProductCode,
    decimal? QualityGrammageGsm,
    bool IsMixedQuality);

public sealed record ExternalProductionRunState(
    long Id,
    string SourceSystem,
    string ExternalRunId,
    string? ProductionOrderCode,
    string? MachineCode,
    bool IsProducing,
    DateTimeOffset? ExpectedEndAtUtc,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    string? QualityKey,
    string? QualityProductCode,
    decimal? QualityGrammageGsm,
    bool IsMixedQuality,
    IReadOnlyList<ExternalProductionItem> Items,
    IReadOnlyList<ExternalProductionReference> References);

public sealed record ProductionIntegrationState(
    string SourceSystem,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessfulSyncAtUtc,
    string Status,
    string? LastError,
    string? LastPayloadHash,
    ExternalProductionRunState? CurrentRun);
