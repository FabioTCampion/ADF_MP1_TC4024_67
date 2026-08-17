using System.Net;
using System.Text.Json.Serialization;

namespace PaperMachine.Historian.Application;

public sealed class WeightExportOptions
{
    public const string SectionName = "WeightExport";

    public bool Enabled { get; set; }
    public string MachineId { get; set; } = "MP1";
    public string BaseUrl { get; set; } = "http://127.0.0.1:5091";
    public string EndpointPath { get; set; } = "/v1/production/weights";
    public string ApiKeyHeaderName { get; set; } = "x-cpnteck-connector-token";
    public string ApiKeyFilePath { get; set; } = string.Empty;
    public int AcceptedCaptureStatus { get; set; } = 20;
    public double MinimumWeightKg { get; set; } = 0.01;
    public double MaximumWeightKg { get; set; } = 100_000;
    public int PollIntervalSeconds { get; set; } = 5;
    public int RequestTimeoutSeconds { get; set; } = 15;
    public int MaximumRetryDelaySeconds { get; set; } = 300;
    public int MaximumResponseBytes { get; set; } = 65_536;

    public void ResolvePaths(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(ApiKeyFilePath))
        {
            ApiKeyFilePath = Path.Combine(dataRoot, "secrets", "production-connector-token.txt");
        }
        else if (!Path.IsPathRooted(ApiKeyFilePath))
        {
            ApiKeyFilePath = Path.GetFullPath(ApiKeyFilePath);
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(MachineId) || MachineId.Length > 32 ||
            MachineId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new InvalidOperationException(
                "WeightExport:MachineId must contain only ASCII letters, numbers, '-' or '_'.");
        }
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.Host, out var address) ||
            !IPAddress.IsLoopback(address) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "WeightExport:BaseUrl must be an absolute loopback HTTP URL without credentials, query or fragment.");
        }
        if (string.IsNullOrWhiteSpace(EndpointPath) || !EndpointPath.StartsWith('/'))
            throw new InvalidOperationException("WeightExport:EndpointPath must start with '/'.");
        if (string.IsNullOrWhiteSpace(ApiKeyHeaderName) ||
            ApiKeyHeaderName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new InvalidOperationException("WeightExport:ApiKeyHeaderName is invalid.");
        if (string.IsNullOrWhiteSpace(ApiKeyFilePath) || !Path.IsPathRooted(ApiKeyFilePath))
            throw new InvalidOperationException("WeightExport:ApiKeyFilePath must be absolute.");
        if (!double.IsFinite(MinimumWeightKg) || !double.IsFinite(MaximumWeightKg) ||
            MinimumWeightKg <= 0 || MaximumWeightKg <= MinimumWeightKg)
            throw new InvalidOperationException("WeightExport weight limits are invalid.");
        if (PollIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("WeightExport:PollIntervalSeconds must be between 1 and 300.");
        if (RequestTimeoutSeconds is < 2 or > 60)
            throw new InvalidOperationException("WeightExport:RequestTimeoutSeconds must be between 2 and 60.");
        if (MaximumRetryDelaySeconds < PollIntervalSeconds || MaximumRetryDelaySeconds > 3_600)
            throw new InvalidOperationException(
                "WeightExport:MaximumRetryDelaySeconds must be between the poll interval and 3600.");
        if (MaximumResponseBytes is < 1_024 or > 1_048_576)
            throw new InvalidOperationException(
                "WeightExport:MaximumResponseBytes must be between 1024 and 1048576.");
    }
}

public sealed record PaperSystemWeightPayload(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("machineId")] string MachineId,
    [property: JsonPropertyName("capturedAtUtc")] string CapturedAtUtc,
    [property: JsonPropertyName("weightKg")] double WeightKg,
    [property: JsonPropertyName("productionMapId")] string ProductionMapId,
    [property: JsonPropertyName("productionOrder")] string? ProductionOrder,
    [property: JsonPropertyName("captureStatus")] int CaptureStatus);

public sealed record WeightExportOutboxItem(
    long Id,
    long CaptureId,
    string EventId,
    string EventType,
    int Revision,
    string PayloadJson,
    int Attempts,
    DateTimeOffset NextAttemptAtUtc);

public sealed record WeightExportQueueStatus(
    bool Enabled,
    bool Configured,
    long PendingCount,
    long SuspendedCount,
    DateTimeOffset? OldestPendingAtUtc,
    DateTimeOffset? LastDeliveredAtUtc,
    string? LastError);

public interface IWeightExportClient
{
    Task<string?> SendAsync(WeightExportOutboxItem item, CancellationToken cancellationToken);
}

public sealed class WeightExportDeliveryException(
    string message,
    bool retryable,
    Exception? innerException = null) : Exception(message, innerException)
{
    public bool Retryable { get; } = retryable;
}
