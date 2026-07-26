using System.Text.Json;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using PaperMachine.Historian.Application;
using PaperMachine.Historian.Domain;
using PaperMachine.Historian.Infrastructure.Ads;
using PaperMachine.Historian.Infrastructure.Database;
using PaperMachine.Historian.Web;

var builder = WebApplication.CreateBuilder(args);
var externalConfigurationPath =
    Environment.GetEnvironmentVariable("PAPER_MACHINE_HISTORIAN_CONFIG");
if (!string.IsNullOrWhiteSpace(externalConfigurationPath))
    builder.Configuration.AddJsonFile(
        Path.GetFullPath(externalConfigurationPath),
        optional: false,
        reloadOnChange: true);
builder.Host.UseWindowsService(options => options.ServiceName = "Paper Machine Historian");
builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

var adsOptions = builder.Configuration
    .GetSection(AdsOptions.SectionName)
    .Get<AdsOptions>() ?? new AdsOptions();
adsOptions.Validate();

var historianOptions = builder.Configuration
    .GetSection(HistorianOptions.SectionName)
    .Get<HistorianOptions>() ?? new HistorianOptions();
historianOptions.Validate();

var databaseOptions = builder.Configuration
    .GetSection(DatabaseOptions.SectionName)
    .Get<DatabaseOptions>() ?? new DatabaseOptions();
if (!Path.IsPathRooted(databaseOptions.FilePath))
    databaseOptions.FilePath = Path.Combine(builder.Environment.ContentRootPath, databaseOptions.FilePath);
databaseOptions.Validate();

var updateOptions = builder.Configuration
    .GetSection(UpdateOptions.SectionName)
    .Get<UpdateOptions>() ?? new UpdateOptions();
updateOptions.ResolvePaths(databaseOptions);
updateOptions.Validate();

var reportingOptions = builder.Configuration
    .GetSection(ReportingOptions.SectionName)
    .Get<ReportingOptions>() ?? new ReportingOptions();
reportingOptions.Validate();

builder.Services.AddSingleton(adsOptions);
builder.Services.AddSingleton(historianOptions);
builder.Services.AddSingleton(databaseOptions);
builder.Services.AddSingleton(updateOptions);
builder.Services.AddSingleton(reportingOptions);
builder.Services.AddSingleton<IPaperMachineReader, AdsPaperMachineReader>();
builder.Services.AddSingleton<IHistorianRepository, SqliteHistorianRepository>();
builder.Services.AddSingleton<IUserRepository, SqliteUserRepository>();
builder.Services.AddSingleton<IProductionBreakReportService, ProductionBreakReportService>();
builder.Services.AddSingleton<IPasswordHasher<ApplicationUser>, PasswordHasher<ApplicationUser>>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HistorianProcessor>();
builder.Services.AddSingleton<HistorianRuntimeState>();
builder.Services.AddSingleton<GitHubReleaseClient>();
builder.Services.AddSingleton<ApplicationUpdateService>();
builder.Services.AddHostedService<HistorianWorker>();
builder.Services.AddHostedService<UpdateCheckWorker>();
builder.Services.AddHealthChecks();
var keyPath = Path.Combine(
    Path.GetDirectoryName(databaseOptions.FilePath)!,
    "keys");
Directory.CreateDirectory(keyPath);
builder.Services
    .AddDataProtection()
    .SetApplicationName("CPNTeck.PaperMachine.Historian")
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath));
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "PaperMachine.Historian.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var identifier = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var users = context.HttpContext.RequestServices.GetRequiredService<IUserRepository>();
            var user = long.TryParse(identifier, out var id)
                ? await users.FindByIdAsync(id, context.HttpContext.RequestAborted)
                : null;
            var sessionRole = context.Principal?.FindFirstValue(ClaimTypes.Role);
            if (user is null || !user.IsActive || sessionRole != user.Role)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter(
        "authentication",
        limiter =>
        {
            limiter.PermitLimit = 5;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
            limiter.AutoReplenishment = true;
        });
    options.AddFixedWindowLimiter(
        "updates",
        limiter =>
        {
            limiter.PermitLimit = 10;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
            limiter.AutoReplenishment = true;
        });
});

var app = builder.Build();

await app.Services.GetRequiredService<IHistorianRepository>()
    .InitializeAsync(CancellationToken.None);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapGet("/health/ready", (HistorianRuntimeState state) =>
{
    var runtime = state.GetStatus();
    var response = new
    {
        ready = runtime.AdsConnected,
        databaseAvailable = true,
        plcOnline = runtime.AdsConnected,
        runtime.LastSuccessfulReadAtUtc,
        runtime.LastError
    };
    return runtime.AdsConnected
        ? Results.Ok(response)
        : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/version", () =>
{
    var assembly = typeof(Program).Assembly.GetName();
    return Results.Ok(new
    {
        product = "CPNTeck Paper Machine Historian",
        version = assembly.Version?.ToString(3) ?? "unknown",
        readOnly = true
    });
});
app.MapHistorianAuthentication();
app.MapHistorianUpdates();
app.MapReportEndpoints();

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/runtime", (HistorianRuntimeState state) => Results.Ok(state.GetStatus()));

api.MapGet("/current", (HistorianRuntimeState state) =>
{
    var snapshot = state.GetSnapshot();
    return snapshot is null
        ? Results.Json(new { message = "No valid PLC snapshot has been captured yet." }, statusCode: 503)
        : Results.Ok(snapshot);
});

api.MapGet(
    "/history/status",
    async (
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? limit,
        IHistorianRepository repository,
        CancellationToken cancellationToken) =>
    {
        var rows = await repository.GetStatusSnapshotsAsync(
            fromUtc,
            toUtc,
            limit ?? 250,
            cancellationToken);
        return Results.Ok(rows.Select(row => new
        {
            row.Id,
            row.CapturedAtUtc,
            Payload = ParseJson(row.PayloadJson),
            row.MappingVersion,
            row.Quality
        }));
    });

api.MapGet(
    "/history/status-changes",
    async (
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? limit,
        IHistorianRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(await repository.GetStatusChangesAsync(
            fromUtc,
            toUtc,
            limit ?? 500,
            cancellationToken)));

api.MapGet(
    "/history/motors",
    async (
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? maxPoints,
        TimeProvider clock,
        IHistorianRepository repository,
        CancellationToken cancellationToken) =>
    {
        var to = (toUtc ?? clock.GetUtcNow()).ToUniversalTime();
        var from = (fromUtc ?? to.AddHours(-8)).ToUniversalTime();
        if (from >= to)
            return Results.BadRequest(new { error = "O início deve ser anterior ao fim do período." });
        if (to - from > TimeSpan.FromDays(31))
            return Results.BadRequest(new { error = "O período máximo para gráficos é de 31 dias." });

        var requestedPoints = maxPoints ?? 1_200;
        if (requestedPoints is < 100 or > 2_000)
            return Results.BadRequest(new { error = "maxPoints deve estar entre 100 e 2000." });

        var rows = await repository.GetTelemetryTrendSamplesAsync(
            from,
            to,
            requestedPoints,
            cancellationToken);
        return Results.Ok(MotorTrendBuilder.Build(rows));
    });

api.MapGet(
    "/history/productivity",
    async (
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        TimeProvider clock,
        IHistorianRepository repository,
        CancellationToken cancellationToken) =>
    {
        var to = (toUtc ?? clock.GetUtcNow()).ToUniversalTime();
        var from = (fromUtc ?? to.AddHours(-8)).ToUniversalTime();
        if (from >= to)
            return Results.BadRequest(new { error = "O início deve ser anterior ao fim do período." });
        if (to - from > TimeSpan.FromDays(7))
            return Results.BadRequest(new { error = "O período máximo para métricas é de 7 dias." });

        return Results.Ok(await repository.GetMachineProductivitySamplesAsync(
            from,
            to,
            cancellationToken));
    });

api.MapGet(
    "/history/commands",
    async (
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? limit,
        IHistorianRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(await repository.GetCommandEventsAsync(
            fromUtc,
            toUtc,
            limit ?? 250,
            cancellationToken)));

api.MapGet(
    "/history/alarms",
    async (
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        bool? active,
        int? limit,
        IHistorianRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(await repository.GetAlarmEventsAsync(
            fromUtc,
            toUtc,
            active,
            limit ?? 250,
            cancellationToken)));

api.MapGet(
    "/history/breaks",
    async (
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int? limit,
        IHistorianRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(await repository.GetPaperBreakEventsAsync(
            fromUtc,
            toUtc,
            limit ?? 250,
            cancellationToken)));

api.MapGet(
    "/history/breaks/{id:long}/diagnostic",
    async (
        long id,
        IHistorianRepository repository,
        CancellationToken cancellationToken) =>
    {
        var diagnostic = await repository.GetPaperBreakDiagnosticAsync(
            id,
            cancellationToken);
        if (diagnostic is null)
            return Results.NotFound(new { error = "Quebra não encontrada." });

        return Results.Ok(new
        {
            diagnostic.Event,
            Samples = diagnostic.Samples.Select(sample => new
            {
                sample.CapturedAtUtc,
                sample.OffsetMilliseconds,
                Status = ParseJson(sample.StatusJson),
                sample.Quality
            }),
            diagnostic.Summary,
            diagnostic.Evidence
        });
    });

api.MapPut(
    "/history/breaks/{id:long}/analysis",
    async (
        long id,
        PaperBreakAnalysisUpdate request,
        HttpContext context,
        TimeProvider clock,
        IHistorianRepository repository,
        CancellationToken cancellationToken) =>
    {
        var allowedStatuses = new[] { "Pendente", "Em análise", "Concluída" };
        if (!allowedStatuses.Contains(request.AnalysisStatus, StringComparer.Ordinal))
            return Results.BadRequest(new { error = "Status de análise inválido." });
        if (request.CauseCategory?.Length > 100 ||
            request.CauseDescription?.Length > 1_000 ||
            request.AnalysisNotes?.Length > 4_000)
        {
            return Results.BadRequest(new { error = "Os campos da análise excedem o limite permitido." });
        }

        var sanitized = new PaperBreakAnalysisUpdate(
            request.AnalysisStatus,
            string.IsNullOrWhiteSpace(request.CauseCategory)
                ? null
                : request.CauseCategory.Trim(),
            string.IsNullOrWhiteSpace(request.CauseDescription)
                ? null
                : request.CauseDescription.Trim(),
            string.IsNullOrWhiteSpace(request.AnalysisNotes)
                ? null
                : request.AnalysisNotes.Trim());
        var updated = await repository.UpdatePaperBreakAnalysisAsync(
            id,
            sanitized,
            context.User.Identity?.Name ?? "usuário",
            clock.GetUtcNow(),
            cancellationToken);
        return updated
            ? Results.NoContent()
            : Results.NotFound(new { error = "Quebra não encontrada." });
    })
    .RequireAuthorization(policy => policy.RequireRole(HistorianRoles.Administrator));

api.MapGet(
    "/storage",
    async (
        IHistorianRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(await repository.GetStorageStatusAsync(cancellationToken)));

app.MapFallbackToFile("index.html");

await app.RunAsync();

static JsonElement ParseJson(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}
