namespace PaperMachine.Historian.Domain;

public sealed record JumboWeightProductionContext(
    long RunId,
    string SourceSystem,
    string ExternalRunId,
    string? ProductionOrderCode,
    string? QualityKey,
    string? ProductCode,
    decimal? GrammageGsm,
    decimal? ProductionWidthMm,
    DateTimeOffset LastSynchronizedAtUtc);

public sealed record JumboWeightCapture(
    long PlcEventCounter,
    long CapturedAtFileTime,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ObservedAtUtc,
    double WeightKg,
    int CaptureStatus,
    string MappingVersion,
    JumboWeightProductionContext? Production);
