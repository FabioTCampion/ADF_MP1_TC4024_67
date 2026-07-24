namespace PaperMachine.Historian.Infrastructure.Database;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string FilePath { get; set; } = "data/PaperMachineHistorian.db";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            throw new InvalidOperationException("Historian database file path is required.");
    }
}
