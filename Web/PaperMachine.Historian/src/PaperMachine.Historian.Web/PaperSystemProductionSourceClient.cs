using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Web;

internal sealed class PaperSystemProductionSourceClient(
    HttpClient httpClient,
    ProductionIntegrationOptions options,
    TimeProvider clock) : IProductionSourceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string SourceSystem => "PaperSystem";

    public async Task<ProductionSourceObservation> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ApiKeyFilePath))
            throw new InvalidOperationException("A credencial da integração ERP ainda não foi configurada.");

        var apiKey = (await File.ReadAllTextAsync(options.ApiKeyFilePath, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("O arquivo da credencial ERP está vazio.");

        return await FetchAsync(httpClient, options, clock, apiKey, cancellationToken);
    }

    internal static async Task<ProductionSourceObservation> FetchAsync(
        HttpClient client,
        ProductionIntegrationOptions integrationOptions,
        TimeProvider timeProvider,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                new Uri(integrationOptions.BaseUrl.TrimEnd('/') + "/"),
                integrationOptions.EndpointPath.TrimStart('/')));
        request.Headers.TryAddWithoutValidation(integrationOptions.ApiKeyHeaderName, apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var source = JsonSerializer.Deserialize<PaperSystemResponse>(payload, JsonOptions)
            ?? throw new InvalidOperationException("O ERP retornou um documento JSON vazio.");
        if (source.ProductionMapId <= 0)
            throw new InvalidOperationException("O ERP retornou um identificador de mapa de produção inválido.");

        return Map(source, payload, timeProvider.GetUtcNow());
    }

    internal static ProductionSourceObservation Map(
        PaperSystemResponse source,
        string rawPayload,
        DateTimeOffset observedAtUtc)
    {
        var items = (source.Items ?? [])
            .Select((item, index) => new ExternalProductionItem(
                index,
                Clean(item.CustomerName),
                FormatCode(item.Order),
                Clean(item.ProductCode),
                item.Format,
                item.Diameter,
                item.Grammage,
                item.PlannedQuantityKg,
                item.ProducedQuantityKg))
            .ToArray();
        var references = (source.Jumbos ?? [])
            .Select((value, index) => new ExternalProductionReference(
                index,
                "Jumbo",
                value.ToString(CultureInfo.InvariantCulture)))
            .ToArray();

        var qualities = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ProductCode) || item.GrammageGsm.HasValue)
            .Select(item => new
            {
                Product = item.ProductCode?.Trim().ToUpperInvariant(),
                Grammage = item.GrammageGsm
            })
            .Distinct()
            .ToArray();
        var mixed = qualities.Length > 1;
        var quality = qualities.Length == 1 ? qualities[0] : null;
        var productionWidthMm = items
            .OrderBy(item => item.Position)
            .Take(3)
            .Where(item => item.Format is > 0)
            .Sum(item => item.Format!.Value);
        decimal? effectiveProductionWidthMm = productionWidthMm > 0
            ? productionWidthMm
            : null;
        var qualityKey = quality is null
            ? mixed ? "MIXED" : null
            : $"{quality.Product ?? "UNKNOWN"}-" +
              $"{quality.Grammage?.ToString("0.###", CultureInfo.InvariantCulture) ?? "UNKNOWN"}-" +
              $"{effectiveProductionWidthMm?.ToString("0.###", CultureInfo.InvariantCulture) ?? "UNKNOWN"}";

        return new ProductionSourceObservation(
            "PaperSystem",
            source.ProductionMapId.ToString(CultureInfo.InvariantCulture),
            FormatCode(source.ProductionOrder),
            Clean(source.Machine),
            source.IsProducing,
            source.ExpectedEndAtUtc?.ToUniversalTime(),
            observedAtUtc.ToUniversalTime(),
            items,
            references,
            rawPayload,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload))),
            qualityKey,
            quality?.Product,
            quality?.Grammage,
            effectiveProductionWidthMm,
            mixed);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FormatCode(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture);

    internal sealed class PaperSystemResponse
    {
        [JsonPropertyName("produzindo")]
        public bool IsProducing { get; init; }

        [JsonPropertyName("idmapaproducao")]
        public long ProductionMapId { get; init; }

        [JsonPropertyName("op")]
        public long? ProductionOrder { get; init; }

        [JsonPropertyName("maquina")]
        public string? Machine { get; init; }

        [JsonPropertyName("datafimproducao")]
        public DateTimeOffset? ExpectedEndAtUtc { get; init; }

        [JsonPropertyName("itens")]
        public IReadOnlyList<PaperSystemItem>? Items { get; init; }

        [JsonPropertyName("jumbos")]
        public IReadOnlyList<long>? Jumbos { get; init; }
    }

    internal sealed class PaperSystemItem
    {
        [JsonPropertyName("desccliente")]
        public string? CustomerName { get; init; }
        [JsonPropertyName("pedido")]
        public long? Order { get; init; }
        [JsonPropertyName("codproduto")]
        public string? ProductCode { get; init; }
        [JsonPropertyName("formato")]
        public decimal? Format { get; init; }
        [JsonPropertyName("diametro")]
        public decimal? Diameter { get; init; }
        [JsonPropertyName("gramatura")]
        public decimal? Grammage { get; init; }
        [JsonPropertyName("quantidadekg")]
        public decimal? PlannedQuantityKg { get; init; }
        [JsonPropertyName("quantidadeproduzidakg")]
        public decimal? ProducedQuantityKg { get; init; }
    }
}
