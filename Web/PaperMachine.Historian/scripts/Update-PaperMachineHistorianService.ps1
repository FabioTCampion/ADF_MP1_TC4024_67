[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Program Files\CPNTeck\PaperMachineHistorian',

    [string]$DataRoot = 'C:\ProgramData\CPNTeck\PaperMachineHistorian',

    [string]$ServiceName = 'CPNTeckPaperMachineHistorian',

    [string]$DisplayName = 'CPNTeck Paper Machine Historian',

    [ValidateRange(1, 65535)]
    [int]$HttpPort = 5088,

    [ValidateSet('0.0.0.0', '127.0.0.1')]
    [string]$BindAddress = '0.0.0.0',

    [string]$AmsNetId = $env:PAPERHISTORIAN_AMS_NET_ID,

    [string]$RemoteIp = $env:PAPERHISTORIAN_REMOTE_IP,

    [switch]$Force,

    [switch]$SkipReadinessChecks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installerPath = Join-Path $PSScriptRoot 'Install-PaperMachineHistorianService.ps1'
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Script de instalacao nao encontrado: $installerPath"
}

if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    throw "Servico '$ServiceName' nao instalado. Use Install-PaperMachineHistorianService.ps1."
}

$arguments = @{
    InstallRoot = $InstallRoot
    DataRoot = $DataRoot
    ServiceName = $ServiceName
    DisplayName = $DisplayName
    HttpPort = $HttpPort
    BindAddress = $BindAddress
    AmsNetId = $AmsNetId
    RemoteIp = $RemoteIp
    Force = $Force
    SkipReadinessChecks = $SkipReadinessChecks
}

& $installerPath @arguments
