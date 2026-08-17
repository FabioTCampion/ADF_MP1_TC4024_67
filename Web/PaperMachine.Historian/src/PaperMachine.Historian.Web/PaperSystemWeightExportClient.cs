using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Web;

internal sealed class PaperSystemWeightExportClient(
    HttpClient httpClient,
    WeightExportOptions options) : IWeightExportClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<string?> SendAsync(
        WeightExportOutboxItem item,
        CancellationToken cancellationToken)
    {
        var localToken = await ReadLocalTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(
                new Uri(options.BaseUrl.TrimEnd('/') + "/"),
                options.EndpointPath.TrimStart('/')));
        request.Headers.TryAddWithoutValidation(options.ApiKeyHeaderName, localToken);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", item.EventId);
        request.Headers.TryAddWithoutValidation("X-Correlation-ID", $"weight-{item.Id}-{item.Revision}");
        request.Content = new StringContent(item.PayloadJson, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var responseBody = await ReadLimitedResponseAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var retryable = response.StatusCode is HttpStatusCode.RequestTimeout or
                HttpStatusCode.TooManyRequests or
                HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden ||
                (int)response.StatusCode >= 500;
            throw new WeightExportDeliveryException(
                $"O conector de pesagens retornou HTTP {(int)response.StatusCode}.",
                retryable);
        }

        PaperSystemWeightResponse? receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<PaperSystemWeightResponse>(responseBody, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new WeightExportDeliveryException(
                "O conector retornou uma confirmação de pesagem inválida.",
                retryable: true,
                exception);
        }
        if (receipt is null ||
            string.IsNullOrWhiteSpace(receipt.EventId) ||
            !string.Equals(receipt.EventId, item.EventId, StringComparison.Ordinal) ||
            !receipt.Applied.HasValue)
        {
            throw new WeightExportDeliveryException(
                "A confirmação do conector não corresponde ao evento enviado.",
                retryable: true);
        }
        return receipt.EventId;
    }

    private async Task<string> ReadLocalTokenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ApiKeyFilePath))
            throw new WeightExportDeliveryException(
                "O token local do conector de pesagens ainda não foi configurado.",
                retryable: true);
        var file = new FileInfo(options.ApiKeyFilePath);
        if (file.Length is <= 0 or > 4_096)
            throw new WeightExportDeliveryException(
                "O arquivo do token local do conector é inválido.",
                retryable: true);
        var token = (await File.ReadAllTextAsync(options.ApiKeyFilePath, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new WeightExportDeliveryException(
                "O token local do conector está vazio.",
                retryable: true);
        return token;
    }

    private async Task<string> ReadLimitedResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > options.MaximumResponseBytes)
            throw new WeightExportDeliveryException(
                "A resposta do conector excedeu o limite permitido.",
                retryable: true);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8_192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > options.MaximumResponseBytes)
                throw new WeightExportDeliveryException(
                    "A resposta do conector excedeu o limite permitido.",
                    retryable: true);
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record PaperSystemWeightResponse(
        [property: JsonPropertyName("eventId")] string? EventId,
        [property: JsonPropertyName("aplicado")] bool? Applied,
        [property: JsonPropertyName("peso")] double? WeightKg);
}
