using System.Text.Json;
using PaperMachine.Historian.Application;
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
builder.Services.AddSingleton<HistorianProcessor>();
builder.Services.AddSingleton<HistorianRuntimeState>();
builder.Services.AddHostedService<HistorianWorker>();
builder.Services.AddHealthChecks();

var app = builder.Build();

await app.Services.GetRequiredService<IHistorianRepository>()
    .InitializeAsync(CancellationToken.None);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHealthChecks("/health");

app.MapGet("/api/runtime", (HistorianRuntimeState state) => Results.Ok(state.GetStatus()));

app.MapGet("/api/current", (HistorianRuntimeState state) =>
{
    var snapshot = state.GetSnapshot();
    return snapshot is null
        ? Results.Json(new { message = "No valid PLC snapshot has been captured yet." }, statusCode: 503)
        : Results.Ok(snapshot);
});

app.MapGet(
    "/api/history/status",
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

app.MapGet(
    "/api/history/commands",
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

app.MapGet(
    "/api/history/alarms",
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
