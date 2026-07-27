using System.Security.Claims;
using System.Text.RegularExpressions;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;

namespace PaperMachine.Historian.Web;

public static partial class BreakAnalysisFilterEndpoints
{
    public const int MaximumFiltersPerUser = 25;
    public const int MaximumVariablesPerFilter = 32;
    public const int MaximumNameLength = 80;
    public const int MaximumVariableNameLength = 128;

    public static void MapBreakAnalysisFilters(this IEndpointRouteBuilder app)
    {
        var filters = app.MapGroup("/api/me/break-analysis-filters")
            .RequireAuthorization();

        filters.MapGet("", ListAsync);
        filters.MapPost("", CreateAsync);
        filters.MapPut("/{id:long}", UpdateAsync);
        filters.MapDelete("/{id:long}", DeleteAsync);
    }

    public static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        IUserBreakAnalysisFilterRepository repository,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        return userId is null
            ? Results.Unauthorized()
            : Results.Ok(await repository.ListAsync(userId.Value, cancellationToken));
    }

    public static async Task<IResult> CreateAsync(
        BreakAnalysisFilterCreateRequest request,
        ClaimsPrincipal principal,
        IUserBreakAnalysisFilterRepository repository,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        if (userId is null)
            return Results.Unauthorized();

        var validationError = Validate(request.Name, request.Variables);
        if (validationError is not null)
            return Results.BadRequest(new { error = validationError });

        var result = await repository.CreateAsync(
            userId.Value,
            request.Name!.Trim(),
            request.Variables!,
            request.IsDefault,
            clock.GetUtcNow(),
            cancellationToken);
        return result.Status switch
        {
            UserBreakAnalysisFilterWriteStatus.Success => Results.Created(
                $"/api/me/break-analysis-filters/{result.Filter!.Id}",
                result.Filter),
            UserBreakAnalysisFilterWriteStatus.NameConflict => Results.Conflict(
                new { error = "Já existe um filtro com esse nome." }),
            UserBreakAnalysisFilterWriteStatus.LimitReached => Results.Conflict(
                new { error = $"O limite de {MaximumFiltersPerUser} filtros por usuário foi atingido." }),
            _ => Results.Problem("Não foi possível salvar o filtro.")
        };
    }

    public static async Task<IResult> UpdateAsync(
        long id,
        BreakAnalysisFilterUpdateRequest request,
        ClaimsPrincipal principal,
        IUserBreakAnalysisFilterRepository repository,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        if (userId is null)
            return Results.Unauthorized();

        var validationError = Validate(request.Name, request.Variables);
        if (validationError is not null)
            return Results.BadRequest(new { error = validationError });
        if (request.Revision < 1)
            return Results.BadRequest(new { error = "A revisão do filtro é inválida." });

        var result = await repository.UpdateAsync(
            id,
            userId.Value,
            request.Name!.Trim(),
            request.Variables!,
            request.IsDefault,
            request.Revision,
            clock.GetUtcNow(),
            cancellationToken);
        return result.Status switch
        {
            UserBreakAnalysisFilterWriteStatus.Success => Results.Ok(result.Filter),
            UserBreakAnalysisFilterWriteStatus.NotFound => Results.NotFound(
                new { error = "Filtro não encontrado." }),
            UserBreakAnalysisFilterWriteStatus.RevisionConflict => Results.Conflict(
                new
                {
                    error = "O filtro foi alterado em outra sessão. Recarregue os filtros e tente novamente.",
                    current = result.Filter
                }),
            UserBreakAnalysisFilterWriteStatus.NameConflict => Results.Conflict(
                new { error = "Já existe um filtro com esse nome." }),
            _ => Results.Problem("Não foi possível atualizar o filtro.")
        };
    }

    public static async Task<IResult> DeleteAsync(
        long id,
        int? revision,
        ClaimsPrincipal principal,
        IUserBreakAnalysisFilterRepository repository,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        if (userId is null)
            return Results.Unauthorized();
        if (revision is null or < 1)
            return Results.BadRequest(new { error = "A revisão do filtro é obrigatória." });

        var status = await repository.DeleteAsync(
            id,
            userId.Value,
            revision.Value,
            cancellationToken);
        return status switch
        {
            UserBreakAnalysisFilterWriteStatus.Success => Results.NoContent(),
            UserBreakAnalysisFilterWriteStatus.NotFound => Results.NotFound(
                new { error = "Filtro não encontrado." }),
            UserBreakAnalysisFilterWriteStatus.RevisionConflict => Results.Conflict(
                new { error = "O filtro foi alterado em outra sessão. Recarregue os filtros e tente novamente." }),
            _ => Results.Problem("Não foi possível excluir o filtro.")
        };
    }

    public static string? Validate(
        string? name,
        IReadOnlyList<string>? variables)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        if (normalizedName.Length is < 1 or > MaximumNameLength)
            return $"O nome deve possuir entre 1 e {MaximumNameLength} caracteres.";
        if (variables is null || variables.Count is < 1 or > MaximumVariablesPerFilter)
            return $"Selecione entre 1 e {MaximumVariablesPerFilter} variáveis.";

        var uniqueVariables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in variables)
        {
            if (string.IsNullOrWhiteSpace(variable) ||
                variable.Length > MaximumVariableNameLength ||
                !TechnicalVariableNamePattern().IsMatch(variable))
            {
                return "A seleção contém um nome técnico de variável inválido.";
            }
            if (!uniqueVariables.Add(variable))
                return "A seleção não pode conter variáveis duplicadas.";
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

public sealed record BreakAnalysisFilterCreateRequest(
    string? Name,
    string[]? Variables,
    bool IsDefault);

public sealed record BreakAnalysisFilterUpdateRequest(
    string? Name,
    string[]? Variables,
    bool IsDefault,
    int Revision);
