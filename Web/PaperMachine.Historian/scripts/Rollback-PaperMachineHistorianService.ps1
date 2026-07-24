[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Program Files\CPNTeck\PaperMachineHistorian',

    [string]$DataRoot = 'C:\ProgramData\CPNTeck\PaperMachineHistorian',

    [string]$ServiceName = 'CPNTeckPaperMachineHistorian',

    [ValidateRange(1, 65535)]
    [int]$HttpPort = 5088,

    [switch]$SkipReadinessChecks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Execute este script em uma janela do PowerShell aberta como Administrador.'
    }
}

function Invoke-ServiceControl {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe falhou com codigo $LASTEXITCODE."
    }
}

function Stop-ServiceSafely {
    param([Parameter(Mandatory = $true)][string]$Name)

    $service = Get-Service -Name $Name -ErrorAction Stop
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $Name -Force
        $service.WaitForStatus(
            [ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(30))
    }
}

function Wait-ForApplication {
    param(
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][bool]$FullCheck
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $lastError = 'A aplicacao ainda nao respondeu.'
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $health = Invoke-WebRequest `
                -Uri "http://127.0.0.1:$Port/health" `
                -UseBasicParsing `
                -TimeoutSec 3
            $frontend = Invoke-WebRequest `
                -Uri "http://127.0.0.1:$Port/" `
                -UseBasicParsing `
                -TimeoutSec 5
            if ($health.StatusCode -ne 200 -or
                $frontend.StatusCode -ne 200 -or
                $frontend.Content -notmatch 'id="root"') {
                $lastError = 'Health ou frontend invalido.'
            }
            elseif (-not $FullCheck) {
                return
            }
            else {
                $readiness = Invoke-RestMethod `
                    -Uri "http://127.0.0.1:$Port/health/ready" `
                    -TimeoutSec 5
                if ($readiness.ready) {
                    return
                }
                $lastError = "Banco=$($readiness.databaseAvailable); ADS=$($readiness.plcOnline)"
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Seconds 1
    }

    throw "Falha na validacao apos rollback: $lastError"
}

Assert-Administrator

$statePath = Join-Path $DataRoot 'deployment-state.json'
if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
    throw "Estado de implantacao nao encontrado: $statePath"
}

$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$currentVersion = [string]$state.CurrentVersion
$previousVersion = [string]$state.PreviousVersion
if ([string]::IsNullOrWhiteSpace($previousVersion)) {
    throw 'Nao existe versao anterior disponivel para rollback.'
}

$releasesRoot = Join-Path $InstallRoot 'releases'
$currentExecutable = Join-Path `
    (Join-Path $releasesRoot $currentVersion) `
    'PaperMachine.Historian.Web.exe'
$previousExecutable = Join-Path `
    (Join-Path $releasesRoot $previousVersion) `
    'PaperMachine.Historian.Web.exe'
if (-not (Test-Path -LiteralPath $previousExecutable -PathType Leaf)) {
    throw "Executavel anterior nao encontrado: $previousExecutable"
}

$service = Get-Service -Name $ServiceName -ErrorAction Stop
Stop-ServiceSafely -Name $ServiceName

try {
    $binaryPath = '"{0}"' -f $previousExecutable
    Invoke-ServiceControl -Arguments @('config', $ServiceName, 'binPath=', $binaryPath)
    Start-Service -Name $ServiceName
    $service.WaitForStatus(
        [ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
    Wait-ForApplication -Port $HttpPort -FullCheck (-not $SkipReadinessChecks)

    $newState = [ordered]@{
        CurrentVersion = $previousVersion
        PreviousVersion = $currentVersion
        RolledBackAtUtc = [DateTime]::UtcNow.ToString('o')
        HttpPort = $HttpPort
    }
    $newState | ConvertTo-Json |
        Set-Content -LiteralPath $statePath -Encoding UTF8

    Write-Host "[OK] Rollback concluido: $currentVersion -> $previousVersion" -ForegroundColor Green
}
catch {
    if (Test-Path -LiteralPath $currentExecutable -PathType Leaf) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $binaryPath = '"{0}"' -f $currentExecutable
        & "$env:SystemRoot\System32\sc.exe" config $ServiceName 'binPath=' $binaryPath | Out-Null
        Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    }
    throw
}
