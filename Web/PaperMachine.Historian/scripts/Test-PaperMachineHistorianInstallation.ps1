[CmdletBinding()]
param(
    [string]$ServiceName = 'CPNTeckPaperMachineHistorian',

    [ValidateRange(1, 65535)]
    [int]$HttpPort = 5088,

    [switch]$SkipAdsCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$service = Get-Service -Name $ServiceName -ErrorAction Stop
$health = Invoke-WebRequest `
    -Uri "http://127.0.0.1:$HttpPort/health" `
    -UseBasicParsing `
    -TimeoutSec 5
$version = Invoke-RestMethod `
    -Uri "http://127.0.0.1:$HttpPort/api/version" `
    -TimeoutSec 5
$frontend = Invoke-WebRequest `
    -Uri "http://127.0.0.1:$HttpPort/" `
    -UseBasicParsing `
    -TimeoutSec 5

if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Running) {
    throw "Servico parado: $($service.Status)"
}
if ($service.StartType -ne [ServiceProcess.ServiceStartMode]::Automatic) {
    throw "Inicio do servico nao esta automatico: $($service.StartType)"
}
if ($health.StatusCode -ne 200) {
    throw "Health retornou HTTP $($health.StatusCode)."
}
if ($frontend.StatusCode -ne 200 -or $frontend.Content -notmatch 'id="root"') {
    throw 'Frontend nao esta disponivel.'
}
if (-not $SkipAdsCheck) {
    $readiness = Invoke-RestMethod `
        -Uri "http://127.0.0.1:$HttpPort/health/ready" `
        -TimeoutSec 5
    if (-not $readiness.ready -or -not $readiness.plcOnline) {
        throw 'Aplicacao iniciou, mas o ADS nao esta conectado.'
    }
}

Write-Host ''
Write-Host '[OK] Instalacao validada.' -ForegroundColor Green
[pscustomobject]@{
    Service = $service.Name
    Status = $service.Status
    StartType = $service.StartType
    Version = $version.version
    ReadOnly = $version.readOnly
    Url = "http://127.0.0.1:$HttpPort"
}
