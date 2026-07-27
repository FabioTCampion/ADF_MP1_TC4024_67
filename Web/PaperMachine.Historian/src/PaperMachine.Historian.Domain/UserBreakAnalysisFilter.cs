namespace PaperMachine.Historian.Domain;

public sealed record UserBreakAnalysisFilter(
    long Id,
    long UserId,
    string Name,
    IReadOnlyList<string> Variables,
    bool IsDefault,
    int Revision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public enum UserBreakAnalysisFilterWriteStatus
{
    Success,
    NotFound,
    RevisionConflict,
    NameConflict,
    LimitReached
}

public sealed record UserBreakAnalysisFilterWriteResult(
    UserBreakAnalysisFilterWriteStatus Status,
    UserBreakAnalysisFilter? Filter = null);
