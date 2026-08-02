[CmdletBinding()]
param(
    [string]$ConnectorDataRoot = 'C:\ProgramData\CPNTeck\ProductionConnector',

    [string]$HistorianDataRoot = 'C:\ProgramData\CPNTeck\PaperMachineHistorian',

    [string]$InstallRoot = 'C:\Program Files\CPNTeck\PaperMachineHistorian',

    [string]$ConnectorExecutablePath,

    [string]$ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$setterPath = Join-Path $PSScriptRoot 'Set-PaperMachineHistorianErpApiKey.ps1'
if (-not (Test-Path -LiteralPath $setterPath -PathType Leaf)) {
    throw "Script de validacao nao encontrado: $setterPath"
}

& $setterPath `
    -ConnectorDataRoot $ConnectorDataRoot `
    -HistorianDataRoot $HistorianDataRoot `
    -InstallRoot $InstallRoot `
    -ConnectorExecutablePath $ConnectorExecutablePath `
    -ResultPath $ResultPath `
    -ValidateOnly `
    -SkipServiceRestart
