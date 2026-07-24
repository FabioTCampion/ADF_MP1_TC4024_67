using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Web;

public sealed class HistorianRuntimeState
{
    private readonly object _gate = new();
    private PaperMachineSnapshot? _snapshot;
    private bool _connected;
    private DateTimeOffset? _lastSuccessfulReadAtUtc;
    private string? _lastError;

    public void SetConnected(PaperMachineSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
            _connected = true;
            _lastSuccessfulReadAtUtc = snapshot.CapturedAtUtc;
            _lastError = null;
        }
    }

    public void SetDisconnected(string error)
    {
        lock (_gate)
        {
            _connected = false;
            _lastError = error;
        }
    }

    public PaperMachineSnapshot? GetSnapshot()
    {
        lock (_gate)
            return _snapshot;
    }

    public HistorianRuntimeStatus GetStatus()
    {
        lock (_gate)
            return new HistorianRuntimeStatus(
                _connected,
                _lastSuccessfulReadAtUtc,
                _lastError,
                _snapshot?.MappingVersion);
    }
}

public sealed record HistorianRuntimeStatus(
    bool AdsConnected,
    DateTimeOffset? LastSuccessfulReadAtUtc,
    string? LastError,
    string? MappingVersion);
