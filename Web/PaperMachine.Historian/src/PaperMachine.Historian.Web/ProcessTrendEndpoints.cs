using System.Text.RegularExpressions;
using PaperMachine.Historian.Application;

namespace PaperMachine.Historian.Web;

public static partial class ProcessTrendEndpoints
{
    public const int MaximumVariables = 24;
    public const int MaximumPoints = 2_000;

    public static void MapProcessTrends(this IEndpointRouteBuilder app)
    {
        var trends = app.MapGroup("/api/history/process-trends")
            .RequireAuthorization();
        trends.MapGet("/catalog", GetCatalog);
        trends.MapGet("", GetAsync);
    }

    public static IResult GetCatalog(HistorianRuntimeState state)
    {
        var snapshot = state.GetSnapshot();
        if (snapshot is null)
            return Results.Ok(Array.Empty<ProcessVariableDefinition>());

        var definitions = snapshot.Status
            .EnumerateObject()
            .Where(property =>
                ProcessVariableCatalog.IsSupportedField(property.Name, property.Value))
            .Select(property => ProcessVariableCatalog.Describe(property.Name))
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FieldName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Results.Ok(definitions);
    }

    public static async Task<IResult> GetAsync(
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? maxPoints,
        string[]? variables,
        TimeProvider clock,
        IHistorianRepository repository,
        CancellationToken cancellationToken)
    {
        var to = (toUtc ?? clock.GetUtcNow()).ToUniversalTime();
        var from = (fromUtc ?? to.AddHours(-8)).ToUniversalTime();
        if (from >= to)
            return Results.BadRequest(new { error = "O início deve ser anterior ao fim do período." });
        if (to - from > TimeSpan.FromDays(31))
            return Results.BadRequest(new { error = "O período máximo para gráficos é de 31 dias." });

        var requestedPoints = maxPoints ?? 1_200;
        if (requestedPoints is < 100 or > MaximumPoints)
            return Results.BadRequest(new { error = $"maxPoints deve estar entre 100 e {MaximumPoints}." });

        var validationError = ValidateVariables(variables);
        if (validationError is not null)
            return Results.BadRequest(new { error = validationError });

        var requestedFields = variables!
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var telemetryFields = TelemetryCatalog.NumericFields
            .Concat(TelemetryCatalog.BooleanFields)
            .ToHashSet(StringComparer.Ordinal);
        var requiresTelemetry = requestedFields.Any(telemetryFields.Contains);
        var requiresSnapshots = requestedFields.Any(field =>
            !telemetryFields.Contains(field) ||
            BestConditionsCatalog.LegacySnapshotFallbackFields.Contains(field));

        var telemetryRows = requiresTelemetry
            ? await repository.GetTelemetryTrendSamplesAsync(
                from,
                to,
                requestedPoints,
                cancellationToken)
            : [];
        var snapshotRows = requiresSnapshots
            ? await repository.GetStatusTrendSamplesAsync(
                from,
                to,
                requestedPoints,
                cancellationToken)
            : [];

        return Results.Ok(ProcessTrendBuilder.Build(
            requestedFields,
            telemetryRows,
            snapshotRows));
    }

    public static string? ValidateVariables(IReadOnlyList<string>? variables)
    {
        if (variables is null || variables.Count is < 1 or > MaximumVariables)
            return $"Selecione entre 1 e {MaximumVariables} variáveis.";

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variable in variables)
        {
            if (string.IsNullOrWhiteSpace(variable) ||
                !TechnicalVariableNamePattern().IsMatch(variable))
            {
                return "A consulta contém uma variável de processo inválida.";
            }
            if (!unique.Add(variable))
                return "A consulta não pode conter variáveis duplicadas.";
        }
        return null;
    }

    [GeneratedRegex(
        @"^[A-Za-z_][A-Za-z0-9_]{0,127}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalVariableNamePattern();
}
