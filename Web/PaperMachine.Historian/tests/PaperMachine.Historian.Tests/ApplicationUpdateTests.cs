using System.IO.Compression;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PaperMachine.Historian.Web;

namespace PaperMachine.Historian.Tests;

public sealed class ApplicationUpdateTests
{
    [Fact]
    public async Task AppliesUpdateRateLimitOnlyToMutationEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ApplicationUpdateService>(_ => null!);
        builder.Services.AddSingleton(new UpdateOptions());
        builder.Services.AddRateLimiter(options =>
            options.AddFixedWindowLimiter(
                "updates",
                limiter =>
                {
                    limiter.PermitLimit = 10;
                    limiter.Window = TimeSpan.FromMinutes(1);
                    limiter.QueueLimit = 0;
                }));
        await using var app = builder.Build();
        app.MapHistorianUpdates();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        foreach (var path in new[]
                 {
                     "/api/updates/status",
                     "/api/updates/logs/latest"
                 })
        {
            var endpoint = Assert.Single(
                endpoints,
                candidate => candidate.RoutePattern.RawText == path);
            Assert.Empty(endpoint.Metadata.OfType<EnableRateLimitingAttribute>());
        }

        foreach (var path in new[]
                 {
                     "/api/updates/check",
                     "/api/updates/download",
                     "/api/updates/install"
                 })
        {
            var endpoint = Assert.Single(
                endpoints,
                candidate => candidate.RoutePattern.RawText == path);
            var limiter = Assert.Single(
                endpoint.Metadata.OfType<EnableRateLimitingAttribute>());
            Assert.Equal("updates", limiter.PolicyName);
        }
    }

    [Theory]
    [InlineData("v0.1.3", "0.1.2", true)]
    [InlineData("0.1.2", "0.1.2", false)]
    [InlineData("0.1.1", "0.1.2", false)]
    [InlineData("0.2.0-beta", "0.1.9", true)]
    public void ComparesReleaseVersions(
        string candidate,
        string current,
        bool expected)
    {
        Assert.Equal(expected, UpdateVersion.IsNewer(candidate, current));
    }

    [Fact]
    public void ParsesSha256Sidecar()
    {
        const string hash =
            "306642EA3E1311657911DF462C5DACF8BFAFED1B4580825455F952EEF85FBE6B";
        Assert.Equal(
            hash,
            UpdatePackageValidator.ParseSha256($"{hash.ToLowerInvariant()}  package.zip"));
    }

    [Fact]
    public void ReadsVersionFromPackagedDeploymentManifest()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.UpdateTests",
            Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(directory, "update.zip");
        Directory.CreateDirectory(directory);
        try
        {
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(
                    "CPNTeck-PaperMachineHistorian-0.1.3-win-x64/deployment-manifest.json");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("""{"Version":"0.1.3"}""");
            }

            Assert.Equal("0.1.3", UpdatePackageValidator.ReadManifestVersion(zipPath));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadsDedicatedInstallErrorWithoutDependingOnCheckError()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.UpdateTests",
            Guid.NewGuid().ToString("N"));
        var updatesDirectory = Path.Combine(directory, "updates");
        Directory.CreateDirectory(updatesDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(updatesDirectory, "update-status.json"),
                """
                {
                  "state": "ready",
                  "lastInstallError": "Falha ao criar o servico do conector.",
                  "lastError": null
                }
                """);
            var options = new UpdateOptions
            {
                Enabled = true,
                WorkingDirectory = updatesDirectory,
                TokenFilePath = Path.Combine(updatesDirectory, "github-token.txt")
            };
            using var client = new GitHubReleaseClient(options);
            var service = new ApplicationUpdateService(
                options,
                client,
                TimeProvider.System,
                NullLogger<ApplicationUpdateService>.Instance);

            var status = service.GetStatus();

            Assert.Equal("ready", status.State);
            Assert.Equal(
                "Falha ao criar o servico do conector.",
                status.LastInstallError);
            Assert.Null(status.LastError);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadsLatestUpdateLogWithLimitsAndCredentialRedaction()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "PaperMachine.Historian.UpdateTests",
            Guid.NewGuid().ToString("N"));
        var updatesDirectory = Path.Combine(directory, "updates");
        var logsDirectory = Path.Combine(updatesDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(logsDirectory, "update-20260801-100000.log"),
                "old log");
            var latestPath = Path.Combine(
                logsDirectory,
                "update-20260802-100000.log");
            File.WriteAllLines(
                latestPath,
                Enumerable.Range(1, 60)
                    .Select(index => index == 60
                        ? "x-api-key: secret-value github_pat_abc123"
                        : $"line {index}"));
            File.SetLastWriteTimeUtc(latestPath, DateTime.UtcNow.AddMinutes(1));

            var options = new UpdateOptions
            {
                Enabled = true,
                WorkingDirectory = updatesDirectory,
                TokenFilePath = Path.Combine(updatesDirectory, "github-token.txt")
            };
            using var client = new GitHubReleaseClient(options);
            var service = new ApplicationUpdateService(
                options,
                client,
                TimeProvider.System,
                NullLogger<ApplicationUpdateService>.Instance);

            var log = service.GetLatestLog(50);

            Assert.True(log.Available);
            Assert.Equal("update-20260802-100000.log", log.FileName);
            Assert.True(log.Truncated);
            Assert.Equal(50, log.Lines.Count);
            Assert.Contains("x-api-key: ***", log.Lines[^1]);
            Assert.DoesNotContain("secret-value", log.Lines[^1]);
            Assert.DoesNotContain("github_pat_", log.Lines[^1]);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
