using System.Security.Claims;

namespace PaperMachine.Historian.Web;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").RequireAuthorization();

        group.MapGet(
            "/production-breaks",
            async (
                DateTimeOffset start,
                DateTimeOffset end,
                double productiveSpeedMpm,
                ClaimsPrincipal principal,
                IProductionBreakReportService service,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var requestedBy = principal.FindFirstValue("display_name")
                        ?? principal.Identity?.Name
                        ?? "Usuário não identificado";
                    var report = await service.GenerateAsync(
                        start,
                        end,
                        productiveSpeedMpm,
                        requestedBy,
                        cancellationToken);
                    return Results.File(report.Content, "application/pdf", report.FileName);
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
                catch (ReportLimitExceededException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Results.StatusCode(499);
                }
                catch (Exception exception)
                {
                    return Results.Problem(
                        title: "Não foi possível gerar o relatório operacional.",
                        detail: exception.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });
    }
}
