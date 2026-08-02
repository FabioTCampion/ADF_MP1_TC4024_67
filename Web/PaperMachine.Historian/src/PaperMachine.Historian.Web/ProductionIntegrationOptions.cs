namespace PaperMachine.Historian.Web;

public sealed class ProductionIntegrationOptions
{
    public const string SectionName = "ProductionIntegration";

    public bool Enabled { get; set; }
    public string Provider { get; set; } = "PaperSystem";
    public string BaseUrl { get; set; } = "http://127.0.0.1:5091";
    public string EndpointPath { get; set; } = "/v1/production/current";
    public int PollIntervalSeconds { get; set; } = 60;
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int StaleAfterSeconds { get; set; } = 180;
    public string ApiKeyHeaderName { get; set; } = "x-cpnteck-connector-token";
    public string ApiKeyFilePath { get; set; } = string.Empty;

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
        if (!Enabled)
            return;
        if (!string.Equals(Provider, "PaperSystem", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsupported production provider '{Provider}'.");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(uri.Host, out var address) ||
            !System.Net.IPAddress.IsLoopback(address) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                "ProductionIntegration:BaseUrl must be an absolute loopback HTTP URL without credentials, query or fragment.");
        if (string.IsNullOrWhiteSpace(EndpointPath) || !EndpointPath.StartsWith('/'))
            throw new InvalidOperationException("ProductionIntegration:EndpointPath must start with '/'.");
        if (PollIntervalSeconds is < 15 or > 3600)
            throw new InvalidOperationException("ProductionIntegration:PollIntervalSeconds must be between 15 and 3600.");
        if (RequestTimeoutSeconds is < 2 or > 60)
            throw new InvalidOperationException("ProductionIntegration:RequestTimeoutSeconds must be between 2 and 60.");
        if (StaleAfterSeconds < PollIntervalSeconds)
            throw new InvalidOperationException("ProductionIntegration:StaleAfterSeconds must not be shorter than the poll interval.");
        if (string.IsNullOrWhiteSpace(ApiKeyHeaderName))
            throw new InvalidOperationException("ProductionIntegration:ApiKeyHeaderName is required.");
        if (string.IsNullOrWhiteSpace(ApiKeyFilePath) || !Path.IsPathRooted(ApiKeyFilePath))
            throw new InvalidOperationException("ProductionIntegration:ApiKeyFilePath must be absolute.");
    }
}
