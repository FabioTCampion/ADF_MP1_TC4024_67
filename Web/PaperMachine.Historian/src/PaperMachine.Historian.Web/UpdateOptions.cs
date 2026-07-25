using PaperMachine.Historian.Infrastructure.Database;

namespace PaperMachine.Historian.Web;

public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    public bool Enabled { get; set; }
    public string Provider { get; set; } = "GitHub";
    public string RepositoryOwner { get; set; } = "FabioTCampion";
    public string RepositoryName { get; set; } = "ADF_MP1_TC4024_67";
    public string ApiBaseUrl { get; set; } = "https://api.github.com";
    public string RuntimeIdentifier { get; set; } = "win-x64";
    public string PackageNamePrefix { get; set; } = "CPNTeck-PaperMachineHistorian";
    public int CheckIntervalMinutes { get; set; } = 30;
    public bool AutoDownload { get; set; } = true;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string TokenFilePath { get; set; } = string.Empty;
    public string UpdaterTaskName { get; set; } = "CPNTeckPaperMachineHistorianUpdater";

    public void ResolvePaths(DatabaseOptions databaseOptions)
    {
        var databaseDirectory = Path.GetDirectoryName(
            Path.GetFullPath(databaseOptions.FilePath))!;
        var dataRoot = Directory.GetParent(databaseDirectory)?.FullName
            ?? databaseDirectory;

        WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory)
            ? Path.Combine(dataRoot, "updates")
            : Path.GetFullPath(WorkingDirectory);
        TokenFilePath = string.IsNullOrWhiteSpace(TokenFilePath)
            ? Path.Combine(WorkingDirectory, "github-token.txt")
            : Path.GetFullPath(TokenFilePath);
    }

    public void Validate()
    {
        if (!string.Equals(Provider, "GitHub", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only the GitHub update provider is supported.");
        if (string.IsNullOrWhiteSpace(RepositoryOwner) ||
            string.IsNullOrWhiteSpace(RepositoryName))
            throw new InvalidOperationException("The update repository is required.");
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var apiUri) ||
            apiUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The update API must use HTTPS.");
        if (CheckIntervalMinutes is < 5 or > 1_440)
            throw new InvalidOperationException(
                "The update check interval must be between 5 and 1440 minutes.");
        if (string.IsNullOrWhiteSpace(PackageNamePrefix) ||
            string.IsNullOrWhiteSpace(RuntimeIdentifier))
            throw new InvalidOperationException("The update package identity is required.");
        if (string.IsNullOrWhiteSpace(WorkingDirectory) ||
            string.IsNullOrWhiteSpace(TokenFilePath))
            throw new InvalidOperationException("The update paths are required.");
        if (string.IsNullOrWhiteSpace(UpdaterTaskName))
            throw new InvalidOperationException("The updater task name is required.");
    }
}

public static class UpdateVersion
{
    public static string Normalize(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];

        var separator = normalized.IndexOfAny(['-', '+']);
        return separator >= 0 ? normalized[..separator] : normalized;
    }

    public static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(Normalize(candidate), out var candidateVersion))
            throw new InvalidOperationException($"Invalid release version: {candidate}");
        if (!Version.TryParse(Normalize(current), out var currentVersion))
            throw new InvalidOperationException($"Invalid installed version: {current}");
        return candidateVersion > currentVersion;
    }
}
