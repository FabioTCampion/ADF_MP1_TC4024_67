using System.Text.Json;
using System.Threading.RateLimiting;
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
builder.Host.UseWindowsService(options => options.ServiceName = "Paper Machine Historian");

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

builder.Services.AddSingleton(adsOptions);
builder.Services.AddSingleton(historianOptions);
builder.Services.AddSingleton(databaseOptions);
builder.Services.AddSingleton<IPaperMachineReader, AdsPaperMachineReader>();
builder.Services.AddSingleton<IHistorianRepository, SqliteHistorianRepository>();
builder.Services.AddSingleton<IUserRepository, SqliteUserRepository>();
builder.Services.AddSingleton<IPasswordHasher<ApplicationUser>, PasswordHasher<ApplicationUser>>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HistorianProcessor>();
builder.Services.AddSingleton<HistorianRuntimeState>();
builder.Services.AddHostedService<HistorianWorker>();
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
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
    options.AddFixedWindowLimiter(
        "authentication",
        limiter =>
        {
            limiter.PermitLimit = 5;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
            limiter.AutoReplenishment = true;
        }));

var app = builder.Build();

await app.Services.GetRequiredService<IHistorianRepository>()
    .InitializeAsync(CancellationToken.None);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapHistorianAuthentication();

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

        var rows = await repository.GetStatusTrendSamplesAsync(
            from,
            to,
            requestedPoints,
            cancellationToken);
        return Results.Ok(MotorTrendBuilder.Build(rows));
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

app.MapFallbackToFile("index.html");

await app.RunAsync();

static JsonElement ParseJson(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}
