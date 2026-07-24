namespace PaperMachine.Historian.Application;

public sealed class HistorianOptions
{
    public const string SectionName = "Historian";

    public int PollIntervalMilliseconds { get; set; } = 1_000;
    public int StatusSnapshotIntervalSeconds { get; set; } = 10;
    public string MappingVersion { get; set; } = "paper-machine-hmi-v1";

    public void Validate()
    {
        if (PollIntervalMilliseconds < 250)
            throw new InvalidOperationException("Historian polling interval must be at least 250 ms.");
        if (StatusSnapshotIntervalSeconds < 1)
            throw new InvalidOperationException("Status snapshot interval must be at least one second.");
        if (string.IsNullOrWhiteSpace(MappingVersion))
            throw new InvalidOperationException("Historian mapping version is required.");
    }
}
