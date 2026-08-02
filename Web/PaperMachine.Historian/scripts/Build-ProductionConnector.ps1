[CmdletBinding()]
param(
    [string]$Version = 'development',

    [string]$OutputDirectory,

    [ValidateSet('windows/amd64', 'linux/amd64', 'linux/arm64')]
    [string[]]$Targets = @('windows/amd64', 'linux/amd64', 'linux/arm64'),

    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$connectorRoot = Join-Path $projectRoot 'src\production-connector'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts\production-connector'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

function Resolve-GoExecutable {
    if (-not [string]::IsNullOrWhiteSpace($env:CPNTECK_GO_EXE) -and
        (Test-Path -LiteralPath $env:CPNTECK_GO_EXE -PathType Leaf)) {
        return [IO.Path]::GetFullPath($env:CPNTECK_GO_EXE)
    }
    $command = Get-Command 'go.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }
    throw 'Go nao encontrado. Instale a versao documentada ou defina CPNTECK_GO_EXE.'
}

function Invoke-Go {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & $script:goExecutable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Go falhou com codigo $LASTEXITCODE."
    }
}

$goExecutable = Resolve-GoExecutable
$environmentNames = @('CGO_ENABLED', 'GOOS', 'GOARCH', 'GOTOOLCHAIN')
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Push-Location -LiteralPath $connectorRoot
try {
    $env:GOTOOLCHAIN = 'local'
    $env:CGO_ENABLED = '0'
    if (-not $SkipTests) {
        Invoke-Go -Arguments @('test', './...')
    }
    foreach ($target in $Targets) {
        $parts = $target.Split('/')
        $env:GOOS = $parts[0]
        $env:GOARCH = $parts[1]
        $extension = if ($env:GOOS -eq 'windows') { '.exe' } else { '' }
        $fileName = "CPNTeck.ProductionConnector-$($env:GOOS)-$($env:GOARCH)$extension"
        $outputPath = Join-Path $OutputDirectory $fileName
        Invoke-Go -Arguments @(
            'build',
            '-trimpath',
            '-buildvcs=false',
            '-ldflags', "-s -w -X main.version=$Version",
            '-o', $outputPath,
            './cmd/cpnteck-production-connector'
        )
        Write-Host "[OK] $target -> $outputPath" -ForegroundColor Green
    }
}
finally {
    Pop-Location
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $previousEnvironment[$name],
            'Process')
    }
}
