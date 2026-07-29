using System.Security.Claims;
using System.Text.RegularExpressions;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Web;

public static partial class GraphLayoutEndpoints
{
    public const int ChartCount = 3;
    public const int MaximumVariablesPerChart = 8;
    public const int MaximumTitleLength = 60;

    public static void MapGraphLayouts(this IEndpointRouteBuilder app)
    {
        var layouts = app.MapGroup("/api/me/graph-layout")
            .RequireAuthorization();
        layouts.MapGet("", GetAsync);
        layouts.MapPut("", SaveAsync);
    }

    public static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        IUserGraphLayoutRepository repository,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        if (userId is null)
            return Results.Unauthorized();

        var layout = await repository.GetAsync(userId.Value, cancellationToken);
        return Results.Ok(layout is null
            ? new GraphLayoutResponse([], 0, null)
            : new GraphLayoutResponse(
                layout.Charts,
                layout.Revision,
                layout.UpdatedAtUtc));
    }

    public static async Task<IResult> SaveAsync(
        GraphLayoutSaveRequest request,
        ClaimsPrincipal principal,
        IUserGraphLayoutRepository repository,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        if (userId is null)
            return Results.Unauthorized();

        var validationError = Validate(request.Charts);
        if (validationError is not null)
            return Results.BadRequest(new { error = validationError });
        if (request.Revision < 0)
            return Results.BadRequest(new { error = "A revisão do layout é inválida." });

        var result = await repository.SaveAsync(
            userId.Value,
            request.Charts!,
            request.Revision,
            clock.GetUtcNow(),
            cancellationToken);
        return result.Status switch
        {
            UserGraphLayoutWriteStatus.Success => Results.Ok(new GraphLayoutResponse(
                result.Layout!.Charts,
                result.Layout.Revision,
                result.Layout.UpdatedAtUtc)),
            UserGraphLayoutWriteStatus.RevisionConflict => Results.Conflict(new
            {
                error = "O layout foi alterado em outra sessão. Recarregue a página e tente novamente.",
                current = result.Layout
            }),
            _ => Results.Problem("Não foi possível salvar o layout.")
        };
    }

    public static string? Validate(IReadOnlyList<UserGraphPanel>? charts)
    {
        if (charts is null || charts.Count != ChartCount)
            return $"O layout deve possuir exatamente {ChartCount} gráficos.";

        foreach (var chart in charts)
        {
            var title = chart.Title?.Trim() ?? string.Empty;
            if (title.Length is < 1 or > MaximumTitleLength)
                return $"Cada título deve possuir entre 1 e {MaximumTitleLength} caracteres.";
            if (chart.Variables is null ||
                chart.Variables.Count is < 1 or > MaximumVariablesPerChart)
            {
                return $"Cada gráfico deve possuir entre 1 e {MaximumVariablesPerChart} variáveis.";
            }

            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var variable in chart.Variables)
            {
                if (string.IsNullOrWhiteSpace(variable) ||
                    !TechnicalVariableNamePattern().IsMatch(variable))
                {
                    return "O layout contém uma variável de processo inválida.";
                }
                if (!unique.Add(variable))
                    return "Um gráfico não pode conter variáveis duplicadas.";
            }
        }
        return null;
    }

    private static long? GetUserId(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;

    [GeneratedRegex(
        @"^[A-Za-z_][A-Za-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalVariableNamePattern();
}

public sealed record GraphLayoutSaveRequest(
    UserGraphPanel[]? Charts,
    int Revision);

public sealed record GraphLayoutResponse(
    IReadOnlyList<UserGraphPanel> Charts,
    int Revision,
    DateTimeOffset? UpdatedAtUtc);
