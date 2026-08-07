using System.Text.Json;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Application;

public static class JumboWeightCaptureDetector
{
    public const string WeightField = "jumboCapturedWeightKg";
    public const string CapturedAtField = "jumboCapturedAtFileTime";
    public const string EventCounterField = "jumboWeightCaptureEventCounter";
    public const string StatusField = "jumboWeightCaptureStatus";

    public static bool TryDetect(
        PaperMachineSnapshot snapshot,
        out JumboWeightCapture? capture)
    {
        capture = null;
        if (snapshot.Status.ValueKind != JsonValueKind.Object ||
            !TryGetUInt64(snapshot.Status, EventCounterField, out var eventCounter) ||
            eventCounter == 0 ||
            eventCounter > long.MaxValue ||
            !TryGetUInt64(snapshot.Status, CapturedAtField, out var fileTime) ||
            fileTime == 0 ||
            fileTime > long.MaxValue ||
            !TryGetDouble(snapshot.Status, WeightField, out var weightKg) ||
            !double.IsFinite(weightKg))
        {
            return false;
        }

        DateTimeOffset capturedAtUtc;
        try
        {
            capturedAtUtc = new DateTimeOffset(
                DateTime.FromFileTimeUtc(checked((long)fileTime)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        var status = TryGetInt32(snapshot.Status, StatusField, out var parsedStatus)
            ? parsedStatus
            : 0;
        capture = new JumboWeightCapture(
            checked((long)eventCounter),
            checked((long)fileTime),
            capturedAtUtc,
            snapshot.CapturedAtUtc,
            weightKg,
            status,
            snapshot.MappingVersion,
            null);
        return true;
    }

    private static bool TryGetUInt64(JsonElement root, string name, out ulong value)
    {
        value = 0;
        return TryGetProperty(root, name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetUInt64(out value);
    }

    private static bool TryGetDouble(JsonElement root, string name, out double value)
    {
        value = 0;
        return TryGetProperty(root, name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value);
    }

    private static bool TryGetInt32(JsonElement root, string name, out int value)
    {
        value = 0;
        return TryGetProperty(root, name, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static bool TryGetProperty(
        JsonElement root,
        string name,
        out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }
}
