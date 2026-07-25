[CmdletBinding()]
param(
    [string]$DataRoot = 'C:\ProgramData\CPNTeck\PaperMachineHistorian',

    [string]$InstallRoot = 'C:\Program Files\CPNTeck\PaperMachineHistorian',

    [string]$ServiceName = 'CPNTeckPaperMachineHistorian',

    [string]$TaskName = 'CPNTeckPaperMachineHistorianUpdater',

    [ValidateRange(1, 65535)]
    [int]$HttpPort = 5088
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Execute este script em uma janela do PowerShell aberta como Administrador.'
}

$sourceScript = Join-Path $PSScriptRoot 'Invoke-PaperMachineHistorianPendingUpdate.ps1'
if (-not (Test-Path -LiteralPath $sourceScript -PathType Leaf)) {
    throw "Script do atualizador nao encontrado: $sourceScript"
}

$updatesRoot = Join-Path $DataRoot 'updates'
$updaterRoot = Join-Path $updatesRoot 'updater'
$stableScript = Join-Path $updaterRoot 'Invoke-PaperMachineHistorianPendingUpdate.ps1'
foreach ($directory in @(
        $updatesRoot,
        $updaterRoot,
        (Join-Path $updatesRoot 'pending'),
        (Join-Path $updatesRoot 'installed'),
        (Join-Path $updatesRoot 'failed'),
        (Join-Path $updatesRoot 'staging'),
        (Join-Path $updatesRoot 'logs'))) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}
Copy-Item -LiteralPath $sourceScript -Destination $stableScript -Force

$arguments = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', ('"{0}"' -f $stableScript),
    '-DataRoot', ('"{0}"' -f $DataRoot),
    '-InstallRoot', ('"{0}"' -f $InstallRoot),
    '-ServiceName', ('"{0}"' -f $ServiceName),
    '-HttpPort', [string]$HttpPort
) -join ' '
$action = New-ScheduledTaskAction `
    -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -Argument $arguments
$taskPrincipal = New-ScheduledTaskPrincipal `
    -UserId 'SYSTEM' `
    -LogonType ServiceAccount `
    -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 20) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Principal $taskPrincipal `
    -Settings $settings `
    -Description 'Instala pacotes validados do CPNTeck Paper Machine Historian.' `
    -Force | Out-Null

Write-Host "[OK] Tarefa de atualizacao instalada: $TaskName" -ForegroundColor Green
