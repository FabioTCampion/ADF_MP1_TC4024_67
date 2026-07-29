namespace PaperMachine.Historian.Domain;

public sealed record UserGraphLayout(
    long UserId,
    IReadOnlyList<UserGraphPanel> Charts,
    int Revision,
    DateTimeOffset UpdatedAtUtc);

public sealed record UserGraphPanel(
    string Title,
    IReadOnlyList<string> Variables);

public enum UserGraphLayoutWriteStatus
{
    Success,
    RevisionConflict
}

public sealed record UserGraphLayoutWriteResult(
    UserGraphLayoutWriteStatus Status,
    UserGraphLayout? Layout = null);
