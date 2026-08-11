using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;

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

        group.MapPut(
            "/{id:long}",
            async (
                long id,
                CorrectJumboWeightRequest request,
                ClaimsPrincipal principal,
                IHistorianRepository repository,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                if (id <= 0)
                    return Results.BadRequest(new { error = "Registro de pesagem inválido." });
                if (!double.IsFinite(request.WeightKg) || request.WeightKg is <= 0 or > 100_000)
                    return Results.BadRequest(new { error = "Informe um peso entre 0,01 e 100.000 kg." });

                var reason = request.Reason?.Trim() ?? string.Empty;
                if (reason.Length is < 5 or > 500)
                    return Results.BadRequest(new { error = "Informe um motivo com 5 a 500 caracteres." });

                var correctedBy = principal.FindFirstValue("display_name")
                    ?? principal.Identity?.Name
                    ?? "Supervisor";
                var updated = await repository.CorrectJumboWeightCaptureAsync(
                    id,
                    request.WeightKg,
                    reason,
                    correctedBy,
                    clock.GetUtcNow(),
                    cancellationToken);
                return updated
                    ? Results.NoContent()
                    : Results.NotFound(new { error = "Pesagem não encontrada." });
            })
            .RequireAuthorization(policy => policy.RequireRole(
                HistorianRoles.Supervisor,
                HistorianRoles.Administrator));

        group.MapDelete(
            "/{id:long}",
            async (
                long id,
                [FromBody] DeleteJumboWeightRequest request,
                ClaimsPrincipal principal,
                IHistorianRepository repository,
                TimeProvider clock,
                CancellationToken cancellationToken) =>
            {
                if (id <= 0)
                    return Results.BadRequest(new { error = "Registro de pesagem inválido." });

                var reason = request.Reason?.Trim() ?? string.Empty;
                if (reason.Length is < 5 or > 500)
                    return Results.BadRequest(new { error = "Informe um motivo com 5 a 500 caracteres." });

                var deletedBy = principal.FindFirstValue("display_name")
                    ?? principal.Identity?.Name
                    ?? "Supervisor";
                var deleted = await repository.DeleteJumboWeightCaptureAsync(
                    id,
                    reason,
                    deletedBy,
                    clock.GetUtcNow(),
                    cancellationToken);
                return deleted
                    ? Results.NoContent()
                    : Results.NotFound(new { error = "Pesagem não encontrada ou já excluída." });
            })
            .RequireAuthorization(policy => policy.RequireRole(
                HistorianRoles.Supervisor,
                HistorianRoles.Administrator));

        return endpoints;
    }

    private sealed record CorrectJumboWeightRequest(double WeightKg, string? Reason);
    private sealed record DeleteJumboWeightRequest(string? Reason);
}
