using System.Security.Claims;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Web;

internal static class UpdateEndpoints
{
    public static void MapHistorianUpdates(this IEndpointRouteBuilder app)
    {
        var updates = app.MapGroup("/api/updates")
            .RequireAuthorization(policy =>
                policy.RequireRole(HistorianRoles.Administrator))
            .RequireRateLimiting("updates");

        updates.MapGet(
            "/status",
            (ApplicationUpdateService service) => Results.Ok(service.GetStatus()));

        updates.MapGet(
            "/logs/latest",
            (int? lines, ApplicationUpdateService service) =>
                Results.Ok(service.GetLatestLog(lines ?? 400)));

        updates.MapPost(
            "/check",
            async (
                ApplicationUpdateService service,
                UpdateOptions options,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    () => service.CheckAsync(options.AutoDownload, cancellationToken)));

        updates.MapPost(
            "/download",
            async (
                ApplicationUpdateService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(
                    () => service.DownloadAsync(cancellationToken)));

        updates.MapPost(
            "/install",
            async (
                InstallUpdateRequest request,
                ClaimsPrincipal principal,
                ApplicationUpdateService service,
                CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var status = await service.RequestInstallAsync(
                        request.Version,
                        principal.Identity?.Name ?? "administrator",
                        cancellationToken);
                    return Results.Accepted("/api/updates/status", status);
                }));
    }

    private static async Task<IResult> ExecuteAsync(
        Func<Task<ApplicationUpdateStatus>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (HttpRequestException exception)
        {
            return Results.Json(
                new { error = exception.Message },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (HttpRequestException exception)
        {
            return Results.Json(
                new { error = exception.Message },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
