namespace PaperMachine.Historian.Web;

public sealed record ApplicationUpdateStatus(
    bool Enabled,
    bool TokenConfigured,
    string Provider,
    string Repository,
    string CurrentVersion,
    string State,
    DateTimeOffset? LastCheckedAtUtc,
    string? AvailableVersion,
    string? ReleaseName,
    string? ReleaseNotes,
    DateTimeOffset? PublishedAtUtc,
    string? PackageFileName,
    long? PackageSizeBytes,
    long DownloadedBytes,
    string? PackageSha256,
    DateTimeOffset? DownloadedAtUtc,
    DateTimeOffset? InstallRequestedAtUtc,
    DateTimeOffset? InstalledAtUtc,
    string? LastInstallError,
    string? LastError);

internal sealed record GitHubReleaseAsset(
    long Id,
    string Name,
    long Size,
    string ApiUrl,
    string? Digest);

internal sealed record GitHubRelease(
    string TagName,
    string Name,
    string Body,
    DateTimeOffset? PublishedAtUtc,
    IReadOnlyList<GitHubReleaseAsset> Assets);

internal sealed class PersistedUpdateState
{
    public string State { get; set; } = "idle";
    public DateTimeOffset? LastCheckedAtUtc { get; set; }
    public string? AvailableVersion { get; set; }
    public string? ReleaseName { get; set; }
    public string? ReleaseNotes { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public string? PackageFileName { get; set; }
    public string? PackagePath { get; set; }
    public long? PackageSizeBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public string? PackageSha256 { get; set; }
    public DateTimeOffset? DownloadedAtUtc { get; set; }
    public DateTimeOffset? InstallRequestedAtUtc { get; set; }
    public DateTimeOffset? InstalledAtUtc { get; set; }
    public string? LastInstallError { get; set; }
    public string? LastError { get; set; }
}

internal sealed record InstallUpdateRequest(string Version);
