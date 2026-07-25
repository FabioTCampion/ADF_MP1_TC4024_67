using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace PaperMachine.Historian.Web;

internal sealed class ApplicationUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly UpdateOptions _options;
    private readonly GitHubReleaseClient _client;
    private readonly TimeProvider _clock;
    private readonly ILogger<ApplicationUpdateService> _logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly string _statusPath;
    private readonly string _requestPath;
    private readonly string _pendingDirectory;
    private readonly string _deploymentStatePath;
    private PersistedUpdateState _state;
    private GitHubRelease? _availableRelease;

    public ApplicationUpdateService(
        UpdateOptions options,
        GitHubReleaseClient client,
        TimeProvider clock,
        ILogger<ApplicationUpdateService> logger)
    {
        _options = options;
        _client = client;
        _clock = clock;
        _logger = logger;
        _statusPath = Path.Combine(options.WorkingDirectory, "update-status.json");
        _requestPath = Path.Combine(options.WorkingDirectory, "update-request.json");
        _pendingDirectory = Path.Combine(options.WorkingDirectory, "pending");
        var dataRoot = Directory.GetParent(options.WorkingDirectory)?.FullName
            ?? options.WorkingDirectory;
        _deploymentStatePath = Path.Combine(dataRoot, "deployment-state.json");

        Directory.CreateDirectory(options.WorkingDirectory);
        Directory.CreateDirectory(_pendingDirectory);
        _state = LoadState();
    }

    public ApplicationUpdateStatus GetStatus()
    {
        lock (_stateLock)
        {
            return new ApplicationUpdateStatus(
                _options.Enabled,
                TokenConfigured(),
                _options.Provider,
                $"{_options.RepositoryOwner}/{_options.RepositoryName}",
                ReadCurrentVersion(),
                _options.Enabled ? _state.State : "disabled",
                _state.LastCheckedAtUtc,
                _state.AvailableVersion,
                _state.ReleaseName,
                _state.ReleaseNotes,
                _state.PublishedAtUtc,
                _state.PackageFileName,
                _state.PackageSizeBytes,
                _state.DownloadedBytes,
                _state.PackageSha256,
                _state.DownloadedAtUtc,
                _state.InstallRequestedAtUtc,
                _state.InstalledAtUtc,
                _state.LastError);
        }
    }

    public async Task<ApplicationUpdateStatus> CheckAsync(
        bool autoDownload,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var updateAvailable = await RefreshReleaseCoreAsync(cancellationToken);
            if (updateAvailable && autoDownload)
                await DownloadCoreAsync(cancellationToken);
            return GetStatus();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ApplicationUpdateStatus> DownloadAsync(
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_availableRelease is null)
            {
                var updateAvailable = await RefreshReleaseCoreAsync(cancellationToken);
                if (!updateAvailable)
                    throw new InvalidOperationException("Não existe uma Release mais nova para baixar.");
            }
            await DownloadCoreAsync(cancellationToken);
            return GetStatus();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ApplicationUpdateStatus> RequestInstallAsync(
        string version,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            string packagePath;
            string packageSha256;
            lock (_stateLock)
            {
                if (_state.State != "ready" ||
                    string.IsNullOrWhiteSpace(_state.PackagePath) ||
                    string.IsNullOrWhiteSpace(_state.PackageSha256) ||
                    string.IsNullOrWhiteSpace(_state.AvailableVersion))
                    throw new InvalidOperationException(
                        "Nenhum pacote validado está pronto para instalação.");
                if (!string.Equals(
                        UpdateVersion.Normalize(version),
                        UpdateVersion.Normalize(_state.AvailableVersion),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "A versão confirmada não corresponde à atualização preparada.");
                packagePath = _state.PackagePath;
                packageSha256 = _state.PackageSha256;
            }

            if (!File.Exists(packagePath))
                throw new InvalidOperationException("O pacote preparado não existe mais.");
            var actualHash = await UpdatePackageValidator.ComputeSha256Async(
                packagePath,
                cancellationToken);
            if (!string.Equals(actualHash, packageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "O pacote preparado falhou na validação SHA-256 final.");

            var requestedAt = _clock.GetUtcNow();
            var request = new
            {
                version = UpdateVersion.Normalize(version),
                packagePath,
                sha256 = packageSha256,
                requestedAtUtc = requestedAt,
                requestedBy
            };
            await WriteJsonAtomicallyAsync(_requestPath, request, cancellationToken);

            lock (_stateLock)
            {
                _state.State = "installRequested";
                _state.InstallRequestedAtUtc = requestedAt;
                _state.LastError = null;
            }
            await SaveStateAsync(cancellationToken);

            try
            {
                await TriggerUpdaterTaskAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                lock (_stateLock)
                {
                    _state.State = "ready";
                    _state.LastError = exception.Message;
                }
                await SaveStateAsync(CancellationToken.None);
                throw;
            }
            return GetStatus();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<bool> RefreshReleaseCoreAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            _state.State = "checking";
            _state.LastError = null;
        }
        await SaveStateAsync(cancellationToken);

        try
        {
            var release = await _client.GetLatestReleaseAsync(cancellationToken);
            var availableVersion = UpdateVersion.Normalize(release.TagName);
            var currentVersion = ReadCurrentVersion();
            var isNewer = UpdateVersion.IsNewer(availableVersion, currentVersion);
            _availableRelease = isNewer ? release : null;

            lock (_stateLock)
            {
                _state.LastCheckedAtUtc = _clock.GetUtcNow();
                _state.AvailableVersion = isNewer ? availableVersion : null;
                _state.ReleaseName = isNewer ? release.Name : null;
                _state.ReleaseNotes = isNewer ? release.Body : null;
                _state.PublishedAtUtc = isNewer ? release.PublishedAtUtc : null;
                _state.State = isNewer ? "available" : "upToDate";
                _state.LastError = null;
                if (!isNewer)
                {
                    _state.PackageFileName = null;
                    _state.PackagePath = null;
                    _state.PackageSizeBytes = null;
                    _state.DownloadedBytes = 0;
                    _state.PackageSha256 = null;
                    _state.DownloadedAtUtc = null;
                }
            }
            await SaveStateAsync(cancellationToken);
            return isNewer;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                _state.State = "error";
                _state.LastCheckedAtUtc = _clock.GetUtcNow();
                _state.LastError = exception.Message;
            }
            await SaveStateAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task DownloadCoreAsync(CancellationToken cancellationToken)
    {
        var release = _availableRelease
            ?? throw new InvalidOperationException("Nenhuma Release foi selecionada para download.");
        var version = UpdateVersion.Normalize(release.TagName);
        var expectedPackageName =
            $"{_options.PackageNamePrefix}-{version}-{_options.RuntimeIdentifier}.zip";
        var packageAsset = release.Assets.SingleOrDefault(asset =>
            string.Equals(asset.Name, expectedPackageName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"A Release {version} não possui o arquivo {expectedPackageName}.");
        var checksumAsset = release.Assets.SingleOrDefault(asset =>
            string.Equals(
                asset.Name,
                $"{expectedPackageName}.sha256",
                StringComparison.OrdinalIgnoreCase));

        string expectedHash;
        if (checksumAsset is not null)
        {
            expectedHash = UpdatePackageValidator.ParseSha256(
                await _client.DownloadTextAsync(checksumAsset, cancellationToken));
        }
        else if (packageAsset.Digest?.StartsWith(
                     "sha256:",
                     StringComparison.OrdinalIgnoreCase) == true)
        {
            expectedHash = UpdatePackageValidator.ParseSha256(
                packageAsset.Digest["sha256:".Length..]);
        }
        else
        {
            throw new InvalidOperationException(
                $"A Release {version} não possui o arquivo de checksum SHA-256.");
        }

        var finalPath = Path.Combine(_pendingDirectory, expectedPackageName);
        var partialPath = $"{finalPath}.partial";
        lock (_stateLock)
        {
            _state.State = "downloading";
            _state.PackageFileName = expectedPackageName;
            _state.PackagePath = null;
            _state.PackageSizeBytes = packageAsset.Size;
            _state.DownloadedBytes = 0;
            _state.PackageSha256 = null;
            _state.DownloadedAtUtc = null;
            _state.LastError = null;
        }
        await SaveStateAsync(cancellationToken);

        try
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
            await _client.DownloadFileAsync(
                packageAsset,
                partialPath,
                downloaded =>
                {
                    lock (_stateLock)
                        _state.DownloadedBytes = downloaded;
                },
                cancellationToken);

            var actualHash = await UpdatePackageValidator.ComputeSha256Async(
                partialPath,
                cancellationToken);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "A atualização baixada não corresponde ao checksum SHA-256.");
            var manifestVersion = UpdatePackageValidator.ReadManifestVersion(partialPath);
            if (!string.Equals(
                    UpdateVersion.Normalize(manifestVersion),
                    version,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "A versão do manifesto não corresponde à Release do GitHub.");

            File.Move(partialPath, finalPath, overwrite: true);
            lock (_stateLock)
            {
                _state.State = "ready";
                _state.PackagePath = finalPath;
                _state.PackageSizeBytes = new FileInfo(finalPath).Length;
                _state.DownloadedBytes = _state.PackageSizeBytes.Value;
                _state.PackageSha256 = actualHash;
                _state.DownloadedAtUtc = _clock.GetUtcNow();
                _state.LastError = null;
            }
            await SaveStateAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
            lock (_stateLock)
            {
                _state.State = "error";
                _state.LastError = exception.Message;
            }
            await SaveStateAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task TriggerUpdaterTaskAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException(
                "A tarefa de atualização só pode ser acionada no Windows.");

        var executable = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/Run");
        startInfo.ArgumentList.Add("/TN");
        startInfo.ArgumentList.Add(_options.UpdaterTaskName);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Não foi possível iniciar a tarefa do atualizador.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException(
                $"A tarefa do atualizador não pôde ser iniciada. {(error + output).Trim()}");
        }
    }

    private PersistedUpdateState LoadState()
    {
        try
        {
            if (!File.Exists(_statusPath))
                return new PersistedUpdateState();
            return JsonSerializer.Deserialize<PersistedUpdateState>(
                       File.ReadAllText(_statusPath),
                       JsonOptions)
                   ?? new PersistedUpdateState();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not read the persisted update status.");
            return new PersistedUpdateState
            {
                State = "error",
                LastError = "The persisted update status could not be read."
            };
        }
    }

    private Task SaveStateAsync(CancellationToken cancellationToken)
    {
        string json;
        lock (_stateLock)
            json = JsonSerializer.Serialize(_state, JsonOptions);
        return WriteTextAtomicallyAsync(_statusPath, json, cancellationToken);
    }

    private static Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken) =>
        WriteTextAtomicallyAsync(
            path,
            JsonSerializer.Serialize(value, JsonOptions),
            cancellationToken);

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporaryPath, value, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private string ReadCurrentVersion()
    {
        try
        {
            if (File.Exists(_deploymentStatePath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_deploymentStatePath));
                if ((document.RootElement.TryGetProperty("CurrentVersion", out var version) ||
                     document.RootElement.TryGetProperty("currentVersion", out version)) &&
                    !string.IsNullOrWhiteSpace(version.GetString()))
                    return UpdateVersion.Normalize(version.GetString()!);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not read deployment-state.json.");
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private bool TokenConfigured() =>
        !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("PAPER_MACHINE_HISTORIAN_GITHUB_TOKEN")) ||
        (File.Exists(_options.TokenFilePath) &&
         new FileInfo(_options.TokenFilePath).Length > 0);

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
            throw new InvalidOperationException(
                "As atualizações automáticas estão desabilitadas na configuração do servidor.");
    }
}
