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

        group.MapGet(
            "/production-breaks/excel",
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
                    var report = await service.GenerateExcelAsync(
                        start,
                        end,
                        productiveSpeedMpm,
                        requestedBy,
                        cancellationToken);
                    return Results.File(
                        report.Content,
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        report.FileName);
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
                        title: "Não foi possível exportar as métricas da máquina.",
                        detail: exception.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

        group.MapGet(
            "/weights/{format}",
            async (
                string format,
                DateTimeOffset start,
                DateTimeOffset end,
                string? search,
                ClaimsPrincipal principal,
                IWeightReportService service,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var requestedBy = principal.FindFirstValue("display_name")
                        ?? principal.Identity?.Name
                        ?? "Usuário não identificado";
                    var report = format.ToLowerInvariant() switch
                    {
                        "pdf" => await service.GeneratePdfAsync(
                            start, end, search, requestedBy, cancellationToken),
                        "excel" or "xlsx" => await service.GenerateExcelAsync(
                            start, end, search, requestedBy, cancellationToken),
                        _ => null
                    };
                    if (report is null)
                        return Results.BadRequest(new { error = "Formato inválido. Use pdf ou excel." });
                    var contentType = format.Equals("pdf", StringComparison.OrdinalIgnoreCase)
                        ? "application/pdf"
                        : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                    return Results.File(report.Content, contentType, report.FileName);
                }
                catch (ArgumentException exception)
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
                        title: "Não foi possível exportar os pesos capturados.",
                        detail: exception.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });
    }
}
