namespace PaperMachine.Historian.Domain;

public static class HistorianRoles
{
    public const string Viewer = "Viewer";
    public const string Supervisor = "Supervisor";
    public const string Administrator = "Administrator";
}

public sealed record ApplicationUser(
    long Id,
    string UserName,
    string DisplayName,
    string PasswordHash,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc);
