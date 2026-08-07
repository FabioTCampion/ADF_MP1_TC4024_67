using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Web;

internal static class JumboWeightCaptureEndpoints
{
    public static IEndpointRouteBuilder MapJumboWeightCaptures(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/production/weights")
            .RequireAuthorization();

        group.MapGet(
            "",
            async (
                DateTimeOffset? fromUtc,
                DateTimeOffset? toUtc,
                int? limit,
                IHistorianRepository repository,
                CancellationToken cancellationToken) =>
            {
                if (fromUtc.HasValue && toUtc.HasValue && fromUtc >= toUtc)
                    return Results.BadRequest(new { error = "O início deve ser anterior ao fim do período." });
                if (fromUtc.HasValue && toUtc.HasValue &&
                    toUtc.Value - fromUtc.Value > TimeSpan.FromDays(366))
                {
                    return Results.BadRequest(new { error = "O período máximo para pesagens é de 366 dias." });
                }

                var requestedLimit = limit ?? 250;
                if (requestedLimit is < 1 or > 5_000)
                    return Results.BadRequest(new { error = "limit deve estar entre 1 e 5000." });

                return Results.Ok(await repository.GetJumboWeightCapturesAsync(
                    fromUtc,
                    toUtc,
                    requestedLimit,
                    cancellationToken));
            });

        group.MapGet(
            "/latest",
            async (
                IHistorianRepository repository,
                CancellationToken cancellationToken) =>
            {
                var captures = await repository.GetJumboWeightCapturesAsync(
                    null,
                    null,
                    1,
                    cancellationToken);
                return captures.Count == 0
                    ? Results.NotFound(new { error = "Nenhuma pesagem de jumbo foi registrada." })
                    : Results.Ok(captures[0]);
            });

        return endpoints;
    }
}
