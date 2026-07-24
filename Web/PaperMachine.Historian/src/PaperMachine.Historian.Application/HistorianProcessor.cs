using System.Text.Json;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Application;

public sealed class HistorianProcessor
{
    private readonly TimeSpan _statusSnapshotInterval;
    private JsonElement? _lastStatus;
    private JsonElement? _lastCommands;
    private JsonElement? _lastAlarms;
    private DateTimeOffset? _lastStatusSnapshotAtUtc;

    public HistorianProcessor(HistorianOptions options)
    {
        options.Validate();
        _statusSnapshotInterval = TimeSpan.FromSeconds(options.StatusSnapshotIntervalSeconds);
    }

    public HistorianCycle Process(PaperMachineSnapshot snapshot)
    {
        EnsureObject(snapshot.Status, nameof(snapshot.Status));
        EnsureObject(snapshot.Commands, nameof(snapshot.Commands));
        EnsureObject(snapshot.Alarms, nameof(snapshot.Alarms));

        var initialObservation = _lastStatus is null;
        var saveStatusSnapshot = initialObservation ||
            !_lastStatusSnapshotAtUtc.HasValue ||
            snapshot.CapturedAtUtc - _lastStatusSnapshotAtUtc.Value >= _statusSnapshotInterval;

        var statusChanges = initialObservation
            ? []
            : FindChanges(_lastStatus!.Value, snapshot.Status, snapshot.CapturedAtUtc);
        var commandChanges = initialObservation
            ? []
            : FindChanges(_lastCommands!.Value, snapshot.Commands, snapshot.CapturedAtUtc);
        var alarmTransitions = FindAlarmTransitions(
            _lastAlarms,
            snapshot.Alarms,
            snapshot.Status,
            snapshot.CapturedAtUtc,
            initialObservation);

        _lastStatus = snapshot.Status.Clone();
        _lastCommands = snapshot.Commands.Clone();
        _lastAlarms = snapshot.Alarms.Clone();
        if (saveStatusSnapshot)
            _lastStatusSnapshotAtUtc = snapshot.CapturedAtUtc;

        return new HistorianCycle(
            snapshot,
            saveStatusSnapshot,
            statusChanges,
            commandChanges,
            alarmTransitions);
    }

    private static IReadOnlyList<FieldChange> FindChanges(
        JsonElement previous,
        JsonElement current,
        DateTimeOffset observedAtUtc)
    {
        var before = previous.EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        var changes = new List<FieldChange>();

        foreach (var item in current.EnumerateObject())
        {
            var currentJson = item.Value.GetRawText();
            if (!before.TryGetValue(item.Name, out var previousValue))
            {
                changes.Add(new FieldChange(item.Name, null, currentJson, observedAtUtc));
                continue;
            }

            var previousJson = previousValue.GetRawText();
            if (!string.Equals(previousJson, currentJson, StringComparison.Ordinal))
                changes.Add(new FieldChange(item.Name, previousJson, currentJson, observedAtUtc));
        }

        return changes;
    }

    private static IReadOnlyList<AlarmTransition> FindAlarmTransitions(
        JsonElement? previous,
        JsonElement current,
        JsonElement currentStatus,
        DateTimeOffset observedAtUtc,
        bool initialObservation)
    {
        var before = previous?.EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.GetBoolean(), StringComparer.OrdinalIgnoreCase);
        var transitions = new List<AlarmTransition>();

        foreach (var item in current.EnumerateObject())
        {
            if (item.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                throw new InvalidOperationException($"Alarm '{item.Name}' is not boolean.");

            var isActive = item.Value.GetBoolean();
            if (initialObservation)
            {
                // The initial false observation closes an alarm left open across an application restart.
                transitions.Add(CreateAlarmTransition(
                    item.Name,
                    isActive,
                    observedAtUtc,
                    true,
                    currentStatus));
                continue;
            }

            if (before is null || !before.TryGetValue(item.Name, out var wasActive) || wasActive != isActive)
            {
                transitions.Add(CreateAlarmTransition(
                    item.Name,
                    isActive,
                    observedAtUtc,
                    false,
                    currentStatus));
            }
        }

        return transitions;
    }

    private static AlarmTransition CreateAlarmTransition(
        string alarmName,
        bool isActive,
        DateTimeOffset observedAtUtc,
        bool initialObservation,
        JsonElement currentStatus) =>
        new(
            alarmName,
            isActive,
            observedAtUtc,
            initialObservation,
            AlarmCatalog.Resolve(alarmName),
            DriveDiagnosticCatalog.Resolve(alarmName, currentStatus));

    private static void EnsureObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{name} must be a JSON object.");
    }
}
