using System.Net;
using System.Text;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Web;

namespace PaperMachine.Historian.Tests;

public sealed class WeightExportTests
{
    [Fact]
    public async Task ClientUsesLocalTokenAndIdempotencyHeader()
    {
        var tokenPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tokenPath, "same-api-key");
            var eventId = "MP1:133998234000000000:1524";
            var handler = new RecordingHandler(async request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("same-api-key", request.Headers.GetValues("x-local-token").Single());
                Assert.Equal(eventId, request.Headers.GetValues("Idempotency-Key").Single());
                Assert.Contains(eventId, await request.Content!.ReadAsStringAsync());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"eventId":"{{eventId}}","aplicado":true,"peso":320}""",
                        Encoding.UTF8,
                        "application/json")
                };
            });
            using var httpClient = new HttpClient(handler);
            var options = new WeightExportOptions
            {
                ApiKeyFilePath = tokenPath,
                ApiKeyHeaderName = "x-local-token"
            };
            var client = new PaperSystemWeightExportClient(httpClient, options);
            var item = new WeightExportOutboxItem(
                1,
                1,
                eventId,
                "captured",
                1,
                $$"""{"eventId":"{{eventId}}"}""",
                0,
                DateTimeOffset.UtcNow);

            Assert.Equal(eventId, await client.SendAsync(item, CancellationToken.None));
        }
        finally
        {
            File.Delete(tokenPath);
        }
    }

    [Fact]
    public async Task ClientClassifiesUnprocessablePayloadAsPermanent()
    {
        var tokenPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tokenPath, "same-api-key");
            using var httpClient = new HttpClient(new RecordingHandler(_ => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent("{\"error\":\"invalid\"}")
                })));
            var client = new PaperSystemWeightExportClient(
                httpClient,
                new WeightExportOptions { ApiKeyFilePath = tokenPath });
            var item = new WeightExportOutboxItem(
                1,
                1,
                "MP1:1:1",
                "captured",
                1,
                "{}",
                0,
                DateTimeOffset.UtcNow);

            var exception = await Assert.ThrowsAsync<WeightExportDeliveryException>(
                () => client.SendAsync(item, CancellationToken.None));
            Assert.False(exception.Retryable);
        }
        finally
        {
            File.Delete(tokenPath);
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
