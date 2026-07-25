[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',

    [string]$RuntimeIdentifier = 'win-x64',

    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $projectRoot 'PaperMachine.Historian.slnx'
$webProjectPath = Join-Path $projectRoot 'src\PaperMachine.Historian.Web\PaperMachine.Historian.Web.csproj'
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$packageName = "CPNTeck-PaperMachineHistorian-$Version-$RuntimeIdentifier"
$packageRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot $packageName))
$publishRoot = Join-Path $packageRoot 'app'
$zipPath = Join-Path $artifactsRoot "$packageName.zip"
$zipHashPath = "$zipPath.sha256"

if (-not $packageRoot.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Calculated package path is outside the artifacts directory.'
}

Assert-CommandAvailable -Name 'dotnet'
Assert-CommandAvailable -Name 'npm.cmd'

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
if (Test-Path -LiteralPath $zipHashPath) {
    Remove-Item -LiteralPath $zipHashPath -Force
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

if (-not $SkipTests) {
    Invoke-CheckedCommand -Command 'dotnet' -Arguments @(
        'test', $solutionPath, '--configuration', 'Release'
    )
}

Invoke-CheckedCommand -Command 'dotnet' -Arguments @(
    'publish', $webProjectPath,
    '--configuration', 'Release',
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--output', $publishRoot,
    "-p:Version=$Version",
    '-p:PublishSingleFile=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)

New-Item -ItemType Directory -Path (Join-Path $packageRoot 'config') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'deploy\appsettings.Production.json') `
    -Destination (Join-Path $packageRoot 'config\appsettings.Production.json')

$deploymentScripts = @(
    'Install-PaperMachineHistorianService.ps1',
    'Update-PaperMachineHistorianService.ps1',
    'Rollback-PaperMachineHistorianService.ps1',
    'Test-PaperMachineHistorianInstallation.ps1',
    'Install-PaperMachineHistorianUpdater.ps1',
    'Invoke-PaperMachineHistorianPendingUpdate.ps1',
    'Set-PaperMachineHistorianUpdateToken.ps1'
)
foreach ($scriptName in $deploymentScripts) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Destination $packageRoot
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'DEPLOYMENT.md') -Destination $packageRoot

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

$manifestFiles = Get-ChildItem -LiteralPath $packageRoot -File -Recurse | ForEach-Object {
    [pscustomobject]@{
        Path = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        Length = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
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
$manifest | ConvertTo-Json -Depth 5 | Set-Content `
    -LiteralPath (Join-Path $packageRoot 'deployment-manifest.json') `
    -Encoding UTF8

Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$zipHash  $([IO.Path]::GetFileName($zipPath))" |
    Set-Content -LiteralPath $zipHashPath -Encoding ASCII

Write-Host ''
Write-Host '[OK] Pacote de implantacao criado.' -ForegroundColor Green
Write-Host $zipPath
Write-Host $zipHashPath
