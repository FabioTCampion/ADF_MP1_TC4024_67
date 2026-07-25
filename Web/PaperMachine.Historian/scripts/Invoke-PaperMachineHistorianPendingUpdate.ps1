[CmdletBinding()]
param(
    [string]$DataRoot = 'C:\ProgramData\CPNTeck\PaperMachineHistorian',

    [string]$InstallRoot = 'C:\Program Files\CPNTeck\PaperMachineHistorian',

    [string]$ServiceName = 'CPNTeckPaperMachineHistorian',

    [ValidateRange(1, 65535)]
    [int]$HttpPort = 5088
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Set-JsonProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        $Value
    )

    if ($null -eq $Object.PSObject.Properties[$Name]) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
    else {
        $Object.$Name = $Value
    }
}

function Write-UpdateStatus {
    param(
        [Parameter(Mandatory = $true)][string]$State,
        [string]$ErrorMessage,
        [string]$Version
    )

    $status = if (Test-Path -LiteralPath $script:statusPath -PathType Leaf) {
        Get-Content -LiteralPath $script:statusPath -Raw | ConvertFrom-Json
    }
    else {
        [pscustomobject]@{}
    }
    Set-JsonProperty -Object $status -Name 'state' -Value $State
    Set-JsonProperty -Object $status -Name 'lastError' -Value $ErrorMessage
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        Set-JsonProperty -Object $status -Name 'availableVersion' -Value $Version
    }
    if ($State -eq 'installing') {
        Set-JsonProperty -Object $status -Name 'installStartedAtUtc' `
            -Value ([DateTime]::UtcNow.ToString('o'))
    }
    if ($State -eq 'succeeded') {
        Set-JsonProperty -Object $status -Name 'installedAtUtc' `
            -Value ([DateTime]::UtcNow.ToString('o'))
        Set-JsonProperty -Object $status -Name 'packagePath' -Value $null
    }

    $temporaryStatus = "$script:statusPath.$([Guid]::NewGuid().ToString('N')).tmp"
    $status | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $temporaryStatus -Encoding UTF8
    Move-Item -LiteralPath $temporaryStatus -Destination $script:statusPath -Force
}

$updatesRoot = Join-Path $DataRoot 'updates'
$pendingRoot = [IO.Path]::GetFullPath((Join-Path $updatesRoot 'pending'))
$installedRoot = Join-Path $updatesRoot 'installed'
$failedRoot = Join-Path $updatesRoot 'failed'
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $updatesRoot 'staging'))
$logsRoot = Join-Path $updatesRoot 'logs'
$requestPath = Join-Path $updatesRoot 'update-request.json'
$script:statusPath = Join-Path $updatesRoot 'update-status.json'
$lockPath = Join-Path $updatesRoot 'update.lock'

foreach ($directory in @(
        $updatesRoot,
        $pendingRoot,
        $installedRoot,
        $failedRoot,
        $stagingRoot,
        $logsRoot)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$logPath = Join-Path $logsRoot (
    'update-{0}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
Start-Transcript -Path $logPath -Force | Out-Null
$lockStream = $null
$stagePath = $null
$packagePath = $null
$version = $null
try {
    try {
        $lockStream = [IO.File]::Open(
            $lockPath,
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    }
    catch {
        throw 'Outra atualizacao ja esta em execucao.'
    }

    if (-not (Test-Path -LiteralPath $requestPath -PathType Leaf)) {
        throw 'Nenhuma solicitacao de atualizacao pendente foi encontrada.'
    }
    $request = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
    $version = [string]$request.version
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Versao solicitada invalida: '$version'."
    }

    $packagePath = [IO.Path]::GetFullPath([string]$request.packagePath)
    $pendingPrefix = $pendingRoot.TrimEnd('\') + '\'
    if (-not $packagePath.StartsWith(
            $pendingPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'O pacote solicitado esta fora da pasta de atualizacoes pendentes.'
    }
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Pacote pendente nao encontrado: $packagePath"
    }
    $actualHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    if ($actualHash -ne ([string]$request.sha256).ToUpperInvariant()) {
        throw 'O pacote pendente falhou na validacao SHA-256.'
    }

    Write-UpdateStatus -State 'installing' -Version $version
    $stagePath = [IO.Path]::GetFullPath((Join-Path $stagingRoot $version))
    $stagingPrefix = $stagingRoot.TrimEnd('\') + '\'
    if (-not $stagePath.StartsWith(
            $stagingPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A pasta temporaria calculada esta fora da area de atualizacao.'
    }
    if (Test-Path -LiteralPath $stagePath) {
        Remove-Item -LiteralPath $stagePath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
    Expand-Archive -LiteralPath $packagePath -DestinationPath $stagePath -Force

    $updateScripts = @(
        Get-ChildItem `
            -LiteralPath $stagePath `
            -Filter 'Update-PaperMachineHistorianService.ps1' `
            -File `
            -Recurse
    )
    if ($updateScripts.Count -ne 1) {
        throw 'O pacote deve conter exatamente um script de atualizacao.'
    }

    & $updateScripts[0].FullName `
        -InstallRoot $InstallRoot `
        -DataRoot $DataRoot `
        -ServiceName $ServiceName `
        -HttpPort $HttpPort
    if (-not $?) {
        throw 'O instalador nao concluiu a atualizacao.'
    }

    $installedPackage = Join-Path $installedRoot ([IO.Path]::GetFileName($packagePath))
    Move-Item -LiteralPath $packagePath -Destination $installedPackage -Force
    Remove-Item -LiteralPath $requestPath -Force
    Write-UpdateStatus -State 'succeeded' -Version $version
    Write-Host "[OK] Atualizacao $version concluida." -ForegroundColor Green
}
catch {
    $message = $_.Exception.Message
    Write-UpdateStatus -State 'failed' -ErrorMessage $message -Version $version
    if ($packagePath -and (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        $failedPackage = Join-Path $failedRoot ([IO.Path]::GetFileName($packagePath))
        Copy-Item -LiteralPath $packagePath -Destination $failedPackage -Force
    }

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Start-Service -Name $ServiceName
    }
    throw
}
finally {
    if ($stagePath -and
        (Test-Path -LiteralPath $stagePath) -and
        $stagePath.StartsWith(
            ($stagingRoot.TrimEnd('\') + '\'),
            [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $stagePath -Recurse -Force
    }
    if ($lockStream) {
        $lockStream.Dispose()
    }
    Stop-Transcript | Out-Null
}
