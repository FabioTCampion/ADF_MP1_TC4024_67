[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Program Files\CPNTeck\PaperMachineHistorian',

    [string]$DataRoot = 'C:\ProgramData\CPNTeck\PaperMachineHistorian',

    [string]$ServiceName = 'CPNTeckPaperMachineHistorian',

    [string]$ConnectorDataRoot = 'C:\ProgramData\CPNTeck\ProductionConnector',

    [string]$ConnectorServiceName = 'CPNTeckProductionConnector',

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

function New-RollbackSafetyBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$ConnectorRoot
    )

    $backupRoot = Join-Path `
        (Join-Path $Root 'backups') `
        ('before-rollback-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path (Join-Path $backupRoot 'data') -Force | Out-Null
    $databasePath = Join-Path $Root 'data\PaperMachineHistorian.db'
    foreach ($databaseFile in @($databasePath, "$databasePath-wal", "$databasePath-shm")) {
        if (Test-Path -LiteralPath $databaseFile -PathType Leaf) {
            Copy-Item -LiteralPath $databaseFile -Destination (Join-Path $backupRoot 'data') -Force
        }
    }
    $configPath = Join-Path $Root 'appsettings.Production.json'
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        Copy-Item -LiteralPath $configPath -Destination $backupRoot -Force
    }
    foreach ($directoryName in @('keys', 'secrets')) {
        $source = Join-Path $Root $directoryName
        if (Test-Path -LiteralPath $source -PathType Container) {
            Copy-Item -LiteralPath $source -Destination $backupRoot -Recurse -Force
        }
    }
    if (Test-Path -LiteralPath $ConnectorRoot -PathType Container) {
        $connectorBackup = Join-Path $backupRoot 'production-connector'
        New-Item -ItemType Directory -Path $connectorBackup -Force | Out-Null
        foreach ($itemName in @('config.json', 'secrets')) {
            $source = Join-Path $ConnectorRoot $itemName
            if (Test-Path -LiteralPath $source) {
                Copy-Item -LiteralPath $source -Destination $connectorBackup -Recurse -Force
            }
        }
    }
    return $backupRoot
}

function Restore-DeploymentBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$BackupPath,
        [Parameter(Mandatory = $true)][string]$ConnectorRoot
    )

    $resolvedBackup = [IO.Path]::GetFullPath($BackupPath)
    $backupsRoot = [IO.Path]::GetFullPath((Join-Path $Root 'backups')).TrimEnd('\') + '\'
    if (-not $resolvedBackup.StartsWith($backupsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup fora da pasta permitida: $resolvedBackup"
    }
    if (-not (Test-Path -LiteralPath $resolvedBackup -PathType Container)) {
        throw "Backup de rollback nao encontrado: $resolvedBackup"
    }

    $databasePath = Join-Path $Root 'data\PaperMachineHistorian.db'
    foreach ($databaseFile in @($databasePath, "$databasePath-wal", "$databasePath-shm")) {
        if (Test-Path -LiteralPath $databaseFile -PathType Leaf) {
            Remove-Item -LiteralPath $databaseFile -Force
        }
    }
    $backupData = Join-Path $resolvedBackup 'data'
    if (Test-Path -LiteralPath $backupData -PathType Container) {
        Copy-Item -Path (Join-Path $backupData '*') -Destination (Split-Path $databasePath) -Force
    }
    $backupConfig = Join-Path $resolvedBackup 'appsettings.Production.json'
    if (Test-Path -LiteralPath $backupConfig -PathType Leaf) {
        Copy-Item -LiteralPath $backupConfig -Destination (Join-Path $Root 'appsettings.Production.json') -Force
    }
    foreach ($directoryName in @('keys', 'secrets')) {
        $source = Join-Path $resolvedBackup $directoryName
        if (Test-Path -LiteralPath $source -PathType Container) {
            Copy-Item -LiteralPath $source -Destination $Root -Recurse -Force
        }
    }
    $connectorBackup = Join-Path $resolvedBackup 'production-connector'
    if (Test-Path -LiteralPath $connectorBackup -PathType Container) {
        New-Item -ItemType Directory -Path $ConnectorRoot -Force | Out-Null
        Copy-Item -Path (Join-Path $connectorBackup '*') -Destination $ConnectorRoot -Recurse -Force
    }
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
$rollbackBackupProperty = $state.PSObject.Properties['RollbackBackupPath']
if ($null -eq $rollbackBackupProperty -or
    [string]::IsNullOrWhiteSpace([string]$rollbackBackupProperty.Value)) {
    throw 'Esta instalacao nao possui backup de dados associado. O rollback automatico foi interrompido para evitar incompatibilidade de schema.'
}
$rollbackBackupPath = [string]$rollbackBackupProperty.Value

$releasesRoot = Join-Path $InstallRoot 'releases'
$currentExecutable = Join-Path `
    (Join-Path $releasesRoot $currentVersion) `
    'PaperMachine.Historian.Web.exe'
$previousExecutable = Join-Path `
    (Join-Path $releasesRoot $previousVersion) `
    'PaperMachine.Historian.Web.exe'
$currentConnectorExecutable = Join-Path `
    (Join-Path (Join-Path $releasesRoot $currentVersion) 'connector') `
    'CPNTeck.ProductionConnector-windows-amd64.exe'
$previousConnectorExecutable = Join-Path `
    (Join-Path (Join-Path $releasesRoot $previousVersion) 'connector') `
    'CPNTeck.ProductionConnector-windows-amd64.exe'
if (-not (Test-Path -LiteralPath $previousExecutable -PathType Leaf)) {
    throw "Executavel anterior nao encontrado: $previousExecutable"
}
$previousHasConnector =
    Test-Path -LiteralPath $previousConnectorExecutable -PathType Leaf

$service = Get-Service -Name $ServiceName -ErrorAction Stop
Stop-ServiceSafely -Name $ServiceName
Stop-ServiceSafely -Name $ConnectorServiceName

$safetyBackupPath = $null
try {
    $safetyBackupPath = New-RollbackSafetyBackup `
        -Root $DataRoot `
        -ConnectorRoot $ConnectorDataRoot
    Restore-DeploymentBackup `
        -Root $DataRoot `
        -BackupPath $rollbackBackupPath `
        -ConnectorRoot $ConnectorDataRoot
    if ($previousHasConnector) {
        $connectorConfigPath = Join-Path $ConnectorDataRoot 'config.json'
        $connectorBinaryPath = '"{0}" --service --config "{1}"' -f `
            $previousConnectorExecutable, $connectorConfigPath
        Invoke-ServiceControl -Arguments @(
            'config', $ConnectorServiceName, 'binPath=', $connectorBinaryPath)
        Invoke-ServiceControl -Arguments @(
            'config', $ServiceName, 'depend=', $ConnectorServiceName)
        Start-Service -Name $ConnectorServiceName
        (Get-Service -Name $ConnectorServiceName).WaitForStatus(
            [ServiceProcess.ServiceControllerStatus]::Running,
            [TimeSpan]::FromSeconds(30))
    }
    else {
        Invoke-ServiceControl -Arguments @(
            'config', $ServiceName, 'depend=', '/')
    }
    $binaryPath = '"{0}"' -f $previousExecutable
    Invoke-ServiceControl -Arguments @('config', $ServiceName, 'binPath=', $binaryPath)
    Start-Service -Name $ServiceName
    $service.WaitForStatus(
        [ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
    Wait-ForApplication -Port $HttpPort -FullCheck (-not $SkipReadinessChecks)

    if (-not $previousHasConnector) {
        Invoke-ServiceControl -Arguments @('delete', $ConnectorServiceName)
    }

    $newState = [ordered]@{
        CurrentVersion = $previousVersion
        PreviousVersion = $currentVersion
        RolledBackAtUtc = [DateTime]::UtcNow.ToString('o')
        HttpPort = $HttpPort
        RollbackBackupPath = $safetyBackupPath
    }
    $newState | ConvertTo-Json |
        Set-Content -LiteralPath $statePath -Encoding UTF8

    Write-Host "[OK] Rollback concluido: $currentVersion -> $previousVersion" -ForegroundColor Green
}
catch {
    if (Test-Path -LiteralPath $currentExecutable -PathType Leaf) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Stop-Service -Name $ConnectorServiceName -Force -ErrorAction SilentlyContinue
        if (-not [string]::IsNullOrWhiteSpace([string]$safetyBackupPath)) {
            Restore-DeploymentBackup `
                -Root $DataRoot `
                -BackupPath $safetyBackupPath `
                -ConnectorRoot $ConnectorDataRoot
        }
        if (Test-Path -LiteralPath $currentConnectorExecutable -PathType Leaf) {
            $connectorConfigPath = Join-Path $ConnectorDataRoot 'config.json'
            $connectorBinaryPath = '"{0}" --service --config "{1}"' -f `
                $currentConnectorExecutable, $connectorConfigPath
            & "$env:SystemRoot\System32\sc.exe" `
                config $ConnectorServiceName 'binPath=' $connectorBinaryPath | Out-Null
            Start-Service -Name $ConnectorServiceName -ErrorAction SilentlyContinue
        }
        $binaryPath = '"{0}"' -f $currentExecutable
        & "$env:SystemRoot\System32\sc.exe" config $ServiceName 'binPath=' $binaryPath | Out-Null
        Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    }
    throw
}
