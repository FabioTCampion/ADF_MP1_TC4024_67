[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',

    [string]$RuntimeIdentifier = 'win-x64',

    [switch]$SkipTests,

    [switch]$SkipClientBuild,

    [switch]$CleanClientDependencies,

    [ValidateSet('Fastest', 'Optimal', 'NoCompression')]
    [string]$CompressionLevel = 'Fastest'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$stepTimings = [System.Collections.Generic.List[object]]::new()
$totalStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

function Assert-CommandAvailable {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found."
    }
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$Command' failed with exit code $LASTEXITCODE."
    }
}

function Invoke-TimedStep {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
    }
    finally {
        $stopwatch.Stop()
        $stepTimings.Add([pscustomobject]@{
            Step = $Name
            Seconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
        })
    }
}

function Remove-ScopedDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$AllowedRoot
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedRoot = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside '$resolvedRoot': $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath) {
        [IO.Directory]::Delete($resolvedPath, $true)
    }
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $projectRoot 'PaperMachine.Historian.slnx'
$webProjectPath =
    Join-Path $projectRoot 'src\PaperMachine.Historian.Web\PaperMachine.Historian.Web.csproj'
$connectorBuildScript = Join-Path $PSScriptRoot 'Build-ProductionConnector.ps1'
$clientRoot = Join-Path $projectRoot 'src\papermachine-web-client'
$packageLockPath = Join-Path $clientRoot 'package-lock.json'
$clientCacheStampPath =
    Join-Path $clientRoot 'node_modules\.cpnteck-package-lock.sha256'
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$packageName = "CPNTeck-PaperMachineHistorian-$Version-$RuntimeIdentifier"
$packageRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot $packageName))
$publishRoot = Join-Path $packageRoot 'app'
$testArtifactsRoot = [IO.Path]::GetFullPath(
    (Join-Path ([IO.Path]::GetTempPath()) 'CPNTeck\PaperMachineHistorianTests'))
$testOutputRoot =
    [IO.Path]::GetFullPath((Join-Path $testArtifactsRoot "$Version-$RuntimeIdentifier"))
$zipPath = Join-Path $artifactsRoot "$packageName.zip"
$zipHashPath = "$zipPath.sha256"
$connectorTarget = switch ($RuntimeIdentifier) {
    'win-x64' { 'windows/amd64' }
    'linux-x64' { 'linux/amd64' }
    'linux-arm64' { 'linux/arm64' }
    default { throw "Runtime sem conector homologado: $RuntimeIdentifier" }
}

if (-not $packageRoot.StartsWith(
    $artifactsRoot.TrimEnd('\') + '\',
    [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Calculated package path is outside the artifacts directory.'
}

Assert-CommandAvailable -Name 'dotnet'
if (-not $SkipClientBuild) {
    Assert-CommandAvailable -Name 'npm.cmd'
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Remove-ScopedDirectory -Path $packageRoot -AllowedRoot $artifactsRoot
New-Item -ItemType Directory -Path $testArtifactsRoot -Force | Out-Null
Remove-ScopedDirectory -Path $testOutputRoot -AllowedRoot $testArtifactsRoot
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
if (Test-Path -LiteralPath $zipHashPath) {
    Remove-Item -LiteralPath $zipHashPath -Force
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

if (-not $SkipClientBuild) {
    if (-not (Test-Path -LiteralPath $packageLockPath)) {
        throw "Client package lock was not found: $packageLockPath"
    }

    $packageLockHash =
        (Get-FileHash -LiteralPath $packageLockPath -Algorithm SHA256).Hash
    $cachedPackageLockHash = if (Test-Path -LiteralPath $clientCacheStampPath) {
        (Get-Content -LiteralPath $clientCacheStampPath -Raw).Trim()
    }
    else {
        $null
    }
    $dependenciesAreCurrent =
        -not $CleanClientDependencies -and
        $cachedPackageLockHash -eq $packageLockHash

    if ($dependenciesAreCurrent) {
        Write-Host ''
        Write-Host '==> Client dependencies: cache hit' -ForegroundColor DarkCyan
    }
    else {
        Invoke-TimedStep -Name 'Restore client dependencies' -Action {
            Push-Location -LiteralPath $clientRoot
            try {
                Invoke-CheckedCommand -Command 'npm.cmd' -Arguments @(
                    'ci',
                    '--prefer-offline',
                    '--no-audit',
                    '--fund=false'
                )
            }
            finally {
                Pop-Location
            }
        }
        New-Item -ItemType Directory `
            -Path (Split-Path -Parent $clientCacheStampPath) `
            -Force | Out-Null
        $packageLockHash |
            Set-Content -LiteralPath $clientCacheStampPath -Encoding ASCII
    }

    Invoke-TimedStep -Name 'Lint client' -Action {
        Push-Location -LiteralPath $clientRoot
        try {
            Invoke-CheckedCommand -Command 'npm.cmd' -Arguments @('run', 'lint')
        }
        finally {
            Pop-Location
        }
    }
    Invoke-TimedStep -Name 'Build client once' -Action {
        Push-Location -LiteralPath $clientRoot
        try {
            Invoke-CheckedCommand -Command 'npm.cmd' -Arguments @('run', 'build')
        }
        finally {
            Pop-Location
        }
    }
}
else {
    Write-Host ''
    Write-Host '==> Client build skipped; existing wwwroot assets will be packaged.' `
        -ForegroundColor Yellow
}

Invoke-TimedStep -Name 'Restore .NET dependencies once' -Action {
    Invoke-CheckedCommand -Command 'dotnet' -Arguments @(
        'restore',
        $solutionPath,
        '--runtime', $RuntimeIdentifier,
        '--nologo'
    )
}

if (-not $SkipTests) {
    try {
        Invoke-TimedStep -Name 'Run tests without locking the local server' -Action {
            Invoke-CheckedCommand -Command 'dotnet' -Arguments @(
                'test',
                $solutionPath,
                '--configuration', 'Release',
                '--no-restore',
                '--nologo',
                "-p:BaseOutputPath=$testOutputRoot/"
            )
        }
    }
    finally {
        Remove-ScopedDirectory -Path $testOutputRoot -AllowedRoot $testArtifactsRoot
    }
}
else {
    Write-Warning 'Tests were skipped. Do not use -SkipTests for an official release.'
}

Invoke-TimedStep -Name 'Publish self-contained application' -Action {
    Invoke-CheckedCommand -Command 'dotnet' -Arguments @(
        'publish', $webProjectPath,
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--no-restore',
        '--output', $publishRoot,
        "-p:Version=$Version",
        '-p:PublishSingleFile=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:SkipClientBuild=true'
    )
}

Invoke-TimedStep -Name "Build production connector ($connectorTarget)" -Action {
    $connectorOutput = Join-Path $publishRoot 'connector'
    & $connectorBuildScript `
        -Version $Version `
        -OutputDirectory $connectorOutput `
        -Targets $connectorTarget `
        -SkipTests:$SkipTests
    if ($LASTEXITCODE -ne 0) {
        throw "Build do conector falhou com codigo $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path (Join-Path $packageRoot 'config') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'deploy\appsettings.Production.json') `
    -Destination (Join-Path $packageRoot 'config\appsettings.Production.json')
Copy-Item -LiteralPath (Join-Path $projectRoot 'deploy\production-connector.config.json') `
    -Destination (Join-Path $packageRoot 'config\production-connector.config.json')

$deploymentScripts = @(
    'Install-PaperMachineHistorianService.ps1',
    'Update-PaperMachineHistorianService.ps1',
    'Rollback-PaperMachineHistorianService.ps1',
    'Test-PaperMachineHistorianInstallation.ps1',
    'Install-PaperMachineHistorianUpdater.ps1',
    'Invoke-PaperMachineHistorianPendingUpdate.ps1',
    'Set-PaperMachineHistorianUpdateToken.ps1',
    'Set-PaperMachineHistorianErpApiKey.ps1',
    'Test-PaperMachineHistorianErpConnection.ps1'
)
foreach ($scriptName in $deploymentScripts) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Destination $packageRoot
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'DEPLOYMENT.md') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'ERP-INTEGRATION.md') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'MULTIPLATFORM.md') -Destination $packageRoot

$commit = 'unavailable'
$gitDirty = $null
if (Get-Command 'git' -ErrorAction SilentlyContinue) {
    $commitOutput = & git -C $projectRoot rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $commitOutput) {
        $commit = ($commitOutput | Select-Object -First 1).Trim()
    }

    $statusOutput = & git -C $projectRoot status --porcelain -- . 2>$null
    if ($LASTEXITCODE -eq 0) {
        $gitDirty = [bool]$statusOutput
    }
}

$manifestFiles = $null
Invoke-TimedStep -Name 'Hash package files' -Action {
    $script:manifestFiles =
        Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            [pscustomobject]@{
                Path = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
                Length = $_.Length
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
}

$manifest = [ordered]@{
    Product = 'CPNTeck Paper Machine Historian'
    Version = $Version
    RuntimeIdentifier = $RuntimeIdentifier
    CreatedAtUtc = [DateTime]::UtcNow.ToString('o')
    GitCommit = $commit
    GitDirty = $gitDirty
    Files = @($manifestFiles)
}
$manifest |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $packageRoot 'deployment-manifest.json') `
        -Encoding UTF8

Invoke-TimedStep -Name "Create ZIP ($CompressionLevel)" -Action {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $compression =
        [System.Enum]::Parse(
            [System.IO.Compression.CompressionLevel],
            $CompressionLevel,
            $true)
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $packageRoot,
        $zipPath,
        $compression,
        $true)
}

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$zipHash  $([IO.Path]::GetFileName($zipPath))" |
    Set-Content -LiteralPath $zipHashPath -Encoding ASCII

$totalStopwatch.Stop()
Write-Host ''
Write-Host '[OK] Pacote de implantacao criado.' -ForegroundColor Green
Write-Host $zipPath
Write-Host $zipHashPath
Write-Host ''
$stepTimings | Format-Table -AutoSize
Write-Host (
    'Tempo total: {0:N2} s' -f $totalStopwatch.Elapsed.TotalSeconds
) -ForegroundColor Green
