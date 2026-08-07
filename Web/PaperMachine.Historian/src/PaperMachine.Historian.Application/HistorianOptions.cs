namespace PaperMachine.Historian.Application;

public sealed class HistorianOptions
{
    public const string SectionName = "Historian";

    public int PollIntervalMilliseconds { get; set; } = 1_000;
    public int TelemetrySampleIntervalSeconds { get; set; } = 5;
    public int StatusSnapshotIntervalSeconds { get; set; } = 60;
    public int DiagnosticSnapshotRetentionDays { get; set; } = 7;
    public int RawTelemetryRetentionDays { get; set; } = 60;
    public int AggregateRetentionDays { get; set; } = 1_825;
    public int StatusChangeRetentionDays { get; set; } = 1_825;
    public int CommunicationEventRetentionDays { get; set; } = 365;
    public int MaintenanceIntervalMinutes { get; set; } = 10;
    public int MaintenanceBatchSize { get; set; } = 5_000;
    public double PaperBreakMinimumSpeedMpm { get; set; } = 5;
    public int PaperBreakDiagnosticWindowSeconds { get; set; } = 180;
    public bool RetentionEnabled { get; set; } = true;
    public string MappingVersion { get; set; } = "paper-machine-hmi-v4";

    public void Validate()
    {
        if (PollIntervalMilliseconds < 250)
            throw new InvalidOperationException("Historian polling interval must be at least 250 ms.");
        if (TelemetrySampleIntervalSeconds < 1)
            throw new InvalidOperationException("Telemetry sample interval must be at least one second.");
        if (StatusSnapshotIntervalSeconds < 1)
            throw new InvalidOperationException("Status snapshot interval must be at least one second.");
        if (DiagnosticSnapshotRetentionDays < 1)
            throw new InvalidOperationException("Diagnostic snapshot retention must be at least one day.");
        if (RawTelemetryRetentionDays < 1)
            throw new InvalidOperationException("Raw telemetry retention must be at least one day.");
        if (AggregateRetentionDays < RawTelemetryRetentionDays)
            throw new InvalidOperationException(
                "Aggregate retention must not be shorter than raw telemetry retention.");
        if (StatusChangeRetentionDays < 1 || CommunicationEventRetentionDays < 1)
            throw new InvalidOperationException("Event retention must be at least one day.");
        if (MaintenanceIntervalMinutes < 1)
            throw new InvalidOperationException("Maintenance interval must be at least one minute.");
        if (MaintenanceBatchSize is < 100 or > 50_000)
            throw new InvalidOperationException("Maintenance batch size must be between 100 and 50000.");
        if (PaperBreakMinimumSpeedMpm < 0)
            throw new InvalidOperationException("Paper-break minimum speed cannot be negative.");
        if (PaperBreakDiagnosticWindowSeconds is < 30 or > 600)
            throw new InvalidOperationException(
                "Paper-break diagnostic window must be between 30 and 600 seconds.");
        if (string.IsNullOrWhiteSpace(MappingVersion))
            throw new InvalidOperationException("Historian mapping version is required.");
    }
}
