using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Web;

internal static class ProductionIntegrationEndpoints
{
    public static IEndpointRouteBuilder MapProductionIntegration(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/production/current",
            async (
                ProductionIntegrationOptions options,
                IProductionIntegrationRepository repository,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                if (!options.Enabled)
                {
                    return Results.Ok(new
                    {
                        enabled = false,
                        configured = File.Exists(options.ApiKeyFilePath),
                        sourceSystem = options.Provider,
                        status = "Disabled",
                        stale = false,
                        lastAttemptAtUtc = (DateTimeOffset?)null,
                        lastSuccessfulSyncAtUtc = (DateTimeOffset?)null,
                        lastError = (string?)null,
                        currentRun = (object?)null
                    });
                }

                var state = await repository.GetStateAsync("PaperSystem", cancellationToken);
                var stale = state.LastSuccessfulSyncAtUtc.HasValue &&
                    clock.GetUtcNow() - state.LastSuccessfulSyncAtUtc.Value >
                    TimeSpan.FromSeconds(options.StaleAfterSeconds);
                return Results.Ok(new
                {
                    enabled = true,
                    configured = File.Exists(options.ApiKeyFilePath),
                    state.SourceSystem,
                    status = stale ? "Stale" : state.Status,
                    stale,
                    state.LastAttemptAtUtc,
                    state.LastSuccessfulSyncAtUtc,
                    state.LastError,
                    state.CurrentRun
                });
            })
            .RequireAuthorization();

        return endpoints;
    }
}
