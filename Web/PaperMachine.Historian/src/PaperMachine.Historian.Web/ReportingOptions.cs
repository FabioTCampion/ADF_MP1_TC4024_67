namespace PaperMachine.Historian.Web;

public sealed class ReportingOptions
{
    public const string SectionName = "Reporting";

    public string MachineName { get; init; } = "Máquina de Papel";
    public int MaximumRangeDays { get; init; } = 31;
    public int MaximumBreaks { get; init; } = 500;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MachineName) || MachineName.Length > 100)
            throw new InvalidOperationException(
                "Reporting:MachineName deve conter entre 1 e 100 caracteres.");
        if (MaximumRangeDays is < 1 or > 31)
            throw new InvalidOperationException(
                "Reporting:MaximumRangeDays deve estar entre 1 e 31.");
        if (MaximumBreaks is < 1 or > 5_000)
            throw new InvalidOperationException(
                "Reporting:MaximumBreaks deve estar entre 1 e 5000.");
    }
}
