using System.Net.Http.Headers;
using System.Text.Json;

namespace PaperMachine.Historian.Web;

internal sealed class GitHubReleaseClient : IDisposable
{
    private readonly UpdateOptions _options;
    private readonly HttpClient _httpClient;

    public GitHubReleaseClient(UpdateOptions options)
    {
        _options = options;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    public async Task<GitHubRelease> GetLatestReleaseAsync(
        CancellationToken cancellationToken)
    {
        var url =
            $"{_options.ApiBaseUrl.TrimEnd('/')}/repos/" +
            $"{Uri.EscapeDataString(_options.RepositoryOwner)}/" +
            $"{Uri.EscapeDataString(_options.RepositoryName)}/releases/latest";
        using var request = CreateRequest(HttpMethod.Get, url, "application/vnd.github+json");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        var assets = new List<GitHubReleaseAsset>();
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            assets.Add(new GitHubReleaseAsset(
                asset.GetProperty("id").GetInt64(),
                asset.GetProperty("name").GetString() ?? string.Empty,
                asset.GetProperty("size").GetInt64(),
                asset.GetProperty("url").GetString() ?? string.Empty,
                asset.TryGetProperty("digest", out var digest)
                    ? digest.GetString()
                    : null));
        }

        return new GitHubRelease(
            root.GetProperty("tag_name").GetString() ?? string.Empty,
            root.TryGetProperty("name", out var name)
                ? name.GetString() ?? string.Empty
                : string.Empty,
            root.TryGetProperty("body", out var body)
                ? body.GetString() ?? string.Empty
                : string.Empty,
            root.TryGetProperty("published_at", out var publishedAt) &&
            publishedAt.ValueKind == JsonValueKind.String &&
            publishedAt.TryGetDateTimeOffset(out var parsedPublishedAt)
                ? parsedPublishedAt
                : null,
            assets);
    }

    public async Task<string> DownloadTextAsync(
        GitHubReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, asset.ApiUrl, "application/octet-stream");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var value = await response.Content.ReadAsStringAsync(cancellationToken);
        if (value.Length > 4_096)
            throw new InvalidOperationException("O arquivo SHA-256 da Release é inválido.");
        return value;
    }

    public async Task DownloadFileAsync(
        GitHubReleaseAsset asset,
        string destinationPath,
        Action<long> progress,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, asset.ApiUrl, "application/octet-stream");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            progress(downloaded);
        }
        await output.FlushAsync(cancellationToken);
    }

    public void Dispose() => _httpClient.Dispose();

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        string accept)
    {
        var token = ReadToken();
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("CPNTeck-PaperMachine-Historian-Updater/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private string ReadToken()
    {
        var environmentToken =
            Environment.GetEnvironmentVariable("PAPER_MACHINE_HISTORIAN_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken))
            return environmentToken.Trim();
        if (!File.Exists(_options.TokenFilePath))
            throw new InvalidOperationException(
                "Token GitHub não configurado. Execute Set-PaperMachineHistorianUpdateToken.ps1.");
        var token = File.ReadAllText(_options.TokenFilePath).Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("O arquivo do token GitHub está vazio.");
        return token;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = response.StatusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized =>
                "O GitHub rejeitou o token de atualização.",
            System.Net.HttpStatusCode.Forbidden =>
                "O token GitHub não pode ler Releases deste repositório privado.",
            System.Net.HttpStatusCode.NotFound =>
                "Nenhuma Release estável acessível foi encontrada no repositório privado.",
            _ => $"O GitHub retornou HTTP {(int)response.StatusCode}."
        };
        if (body.Length > 300)
            body = body[..300];
        throw new HttpRequestException($"{detail} {body}".Trim());
    }
}
