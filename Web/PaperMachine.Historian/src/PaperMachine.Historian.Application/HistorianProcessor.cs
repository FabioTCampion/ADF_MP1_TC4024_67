using System.Text.Json;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Application;

public sealed class HistorianProcessor
{
    private readonly TimeSpan _telemetrySampleInterval;
    private readonly TimeSpan _statusSnapshotInterval;
    private readonly TimeSpan _paperBreakDiagnosticWindow;
    private readonly double _paperBreakMinimumSpeedMpm;
    private readonly Queue<PaperBreakDiagnosticSample> _paperBreakBuffer = new();
    private JsonElement? _lastStatus;
    private JsonElement? _lastCommands;
    private JsonElement? _lastAlarms;
    private DateTimeOffset? _lastTelemetrySampleAtUtc;
    private DateTimeOffset? _lastStatusSnapshotAtUtc;
    private bool? _lastPaperBreakActive;

    public HistorianProcessor(HistorianOptions options)
    {
        options.Validate();
        _telemetrySampleInterval = TimeSpan.FromSeconds(options.TelemetrySampleIntervalSeconds);
        _statusSnapshotInterval = TimeSpan.FromSeconds(options.StatusSnapshotIntervalSeconds);
        _paperBreakMinimumSpeedMpm = options.PaperBreakMinimumSpeedMpm;
        _paperBreakDiagnosticWindow =
            TimeSpan.FromSeconds(options.PaperBreakDiagnosticWindowSeconds);
    }

    public HistorianCycle Process(PaperMachineSnapshot snapshot)
    {
        EnsureObject(snapshot.Status, nameof(snapshot.Status));
        EnsureObject(snapshot.Commands, nameof(snapshot.Commands));
        EnsureObject(snapshot.Alarms, nameof(snapshot.Alarms));

        var initialObservation = _lastStatus is null;
        var saveTelemetrySample = initialObservation ||
            !_lastTelemetrySampleAtUtc.HasValue ||
            snapshot.CapturedAtUtc - _lastTelemetrySampleAtUtc.Value >= _telemetrySampleInterval;
        var saveStatusSnapshot = initialObservation ||
            !_lastStatusSnapshotAtUtc.HasValue ||
            snapshot.CapturedAtUtc - _lastStatusSnapshotAtUtc.Value >= _statusSnapshotInterval;

        var statusChanges = initialObservation
            ? []
            : FindChanges(
                _lastStatus!.Value,
                snapshot.Status,
                snapshot.CapturedAtUtc,
                TelemetryCatalog.IsDiscreteStatusField);
        var commandChanges = initialObservation
            ? CaptureInitialCommandValues(snapshot.Commands, snapshot.CapturedAtUtc)
            : FindChanges(
                _lastCommands!.Value,
                snapshot.Commands,
                snapshot.CapturedAtUtc,
                static (_, _) => true);
        var alarmTransitions = FindAlarmTransitions(
            _lastAlarms,
            snapshot.Alarms,
            snapshot.Status,
            snapshot.CapturedAtUtc,
            initialObservation);
        var paperBreakTransitions = FindPaperBreakTransitions(
            snapshot.Status,
            snapshot.CapturedAtUtc,
            initialObservation);
        AddPaperBreakDiagnosticSample(snapshot);
        var paperBreakDiagnostics = paperBreakTransitions
            .Where(transition => transition.IsActive && !transition.InitialObservation)
            .Select(transition => new PaperBreakDiagnosticCapture(
                transition.ObservedAtUtc,
                _paperBreakBuffer
                    .Where(sample =>
                        sample.CapturedAtUtc >= transition.ObservedAtUtc - _paperBreakDiagnosticWindow &&
                        sample.CapturedAtUtc <= transition.ObservedAtUtc)
                    .ToArray()))
            .ToArray();

        _lastStatus = snapshot.Status.Clone();
        _lastCommands = snapshot.Commands.Clone();
        _lastAlarms = snapshot.Alarms.Clone();
        if (saveTelemetrySample)
            _lastTelemetrySampleAtUtc = snapshot.CapturedAtUtc;
        if (saveStatusSnapshot)
            _lastStatusSnapshotAtUtc = snapshot.CapturedAtUtc;

        return new HistorianCycle(
            snapshot,
            saveTelemetrySample,
            saveStatusSnapshot,
            statusChanges,
            commandChanges,
            alarmTransitions,
            paperBreakTransitions,
            paperBreakDiagnostics);
    }

    private static IReadOnlyList<FieldChange> CaptureInitialCommandValues(
        JsonElement commands,
        DateTimeOffset observedAtUtc) =>
        commands.EnumerateObject()
            .Where(item => TelemetryCatalog.BaselineCommandFields.Contains(item.Name))
            .Select(item => new FieldChange(
                item.Name,
                null,
                item.Value.GetRawText(),
                observedAtUtc))
            .ToArray();

    private void AddPaperBreakDiagnosticSample(PaperMachineSnapshot snapshot)
    {
        _paperBreakBuffer.Enqueue(new PaperBreakDiagnosticSample(
            snapshot.CapturedAtUtc,
            snapshot.Status.Clone()));

        var oldestAllowed = snapshot.CapturedAtUtc - _paperBreakDiagnosticWindow;
        while (_paperBreakBuffer.TryPeek(out var oldest) &&
               oldest.CapturedAtUtc < oldestAllowed)
        {
            _paperBreakBuffer.Dequeue();
        }
    }

    private static IReadOnlyList<FieldChange> FindChanges(
        JsonElement previous,
        JsonElement current,
        DateTimeOffset observedAtUtc,
        Func<string, JsonElement, bool> shouldPersist)
    {
        var before = previous.EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        var changes = new List<FieldChange>();

        foreach (var item in current.EnumerateObject())
        {
            if (!shouldPersist(item.Name, item.Value))
                continue;

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

    private IReadOnlyList<PaperBreakTransition> FindPaperBreakTransitions(
        JsonElement status,
        DateTimeOffset observedAtUtc,
        bool initialObservation)
    {
        var paperPresent = TelemetryCatalog.TryReadEffectivePaperPresence(status);
        var speedMpm = TryReadNumber(status, TelemetryCatalog.MachineSpeedField) ?? 0;
        if (!paperPresent.HasValue)
            return [];

        var isActive = !paperPresent.Value && speedMpm >= _paperBreakMinimumSpeedMpm;
        if (initialObservation || !_lastPaperBreakActive.HasValue ||
            _lastPaperBreakActive.Value != isActive)
        {
            _lastPaperBreakActive = isActive;
            return
            [
                new PaperBreakTransition(
                    isActive,
                    observedAtUtc,
                    initialObservation,
                    speedMpm)
            ];
        }

        _lastPaperBreakActive = isActive;
        return [];
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

    private static bool? TryReadBoolean(JsonElement value, string fieldName)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, fieldName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.Value.GetBoolean();
            }
        }

        return null;
    }

    private static double? TryReadNumber(JsonElement value, string fieldName)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, fieldName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetDouble(out var number))
            {
                return number;
            }
        }

        return null;
    }
}
