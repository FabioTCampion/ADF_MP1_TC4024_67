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

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Execute este script em uma janela do PowerShell aberta como Administrador.'
    }
}

function Assert-AmsNetId {
    param([Parameter(Mandatory = $true)][string]$Value)

    $segments = $Value.Split('.')
    if ($segments.Count -ne 6) {
        throw 'O AMS Net ID deve possuir seis segmentos numericos, por exemplo 192.168.100.1.1.1.'
    }
    foreach ($segment in $segments) {
        [byte]$parsed = 0
        if (-not [byte]::TryParse($segment, [ref]$parsed)) {
            throw "Segmento invalido no AMS Net ID: '$segment'."
        }
    }
}

function Assert-IpAddress {
    param([Parameter(Mandatory = $true)][string]$Value)

    [Net.IPAddress]$parsed = $null
    if (-not [Net.IPAddress]::TryParse($Value, [ref]$parsed)) {
        throw "Endereco IP remoto invalido: '$Value'."
    }
}

function Invoke-ServiceControl {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & "$env:SystemRoot\System32\sc.exe" @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe falhou com codigo $LASTEXITCODE. Argumentos: $($Arguments -join ' ')"
    }
}

function Stop-ExistingService {
    param([Parameter(Mandatory = $true)][string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return $false
    }

    $wasRunning = $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped
    if ($wasRunning) {
        Stop-Service -Name $Name -Force
        $service.WaitForStatus(
            [ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(30))
    }
    return $wasRunning
}

function New-DeploymentBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$PreviousVersion
    )

    $databasePath = Join-Path $Root 'data\PaperMachineHistorian.db'
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) {
        return
    }

    $versionLabel = if ([string]::IsNullOrWhiteSpace($PreviousVersion)) {
        'unknown'
    }
    else {
        $PreviousVersion -replace '[^0-9A-Za-z._-]', '_'
    }
    $backupRoot = Join-Path `
        (Join-Path $Root 'backups') `
        ('before-{0}-{1}' -f $versionLabel, (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $backupRoot 'data') -Force | Out-Null

    foreach ($databaseFile in @(
            $databasePath,
            "$databasePath-wal",
            "$databasePath-shm")) {
        if (Test-Path -LiteralPath $databaseFile -PathType Leaf) {
            Copy-Item `
                -LiteralPath $databaseFile `
                -Destination (Join-Path $backupRoot 'data') `
                -Force
        }
    }
    $configPath = Join-Path $Root 'appsettings.Production.json'
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        Copy-Item -LiteralPath $configPath -Destination $backupRoot -Force
    }
    $keysPath = Join-Path $Root 'keys'
    if (Test-Path -LiteralPath $keysPath -PathType Container) {
        Copy-Item -LiteralPath $keysPath -Destination $backupRoot -Recurse -Force
    }

    $oldBackups = @(
        Get-ChildItem `
            -LiteralPath (Join-Path $Root 'backups') `
            -Directory `
            -Filter 'before-*' |
            Sort-Object LastWriteTime -Descending |
            Select-Object -Skip 5
    )
    foreach ($oldBackup in $oldBackups) {
        $resolvedBackup = [IO.Path]::GetFullPath($oldBackup.FullName)
        $backupsRoot = [IO.Path]::GetFullPath((Join-Path $Root 'backups')).TrimEnd('\') + '\'
        if ($resolvedBackup.StartsWith(
                $backupsRoot,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedBackup -Recurse -Force
        }
    }
}

function Set-ServiceBinaryPath {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ExecutablePath
    )

    $quotedPath = '"' + $ExecutablePath + '"'
    Invoke-ServiceControl -Arguments @(
        'config', $Name,
        'binPath=', $quotedPath,
        'start=', 'delayed-auto'
    )
}

function Test-PackageIntegrity {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)]$Manifest
    )

    foreach ($file in $Manifest.Files) {
        $path = Join-Path $Root ($file.Path -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Arquivo ausente no pacote: $($file.Path)"
        }
        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($actualHash -ne $file.Sha256) {
            throw "Falha de integridade no arquivo: $($file.Path)"
        }
    }
}

function Start-AndValidateService {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][bool]$SkipFullReadiness
    )

    Start-Service -Name $Name
    (Get-Service -Name $Name).WaitForStatus(
        [ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $lastReason = 'A aplicacao ainda nao respondeu.'
    do {
        try {
            $health = Invoke-WebRequest `
                -Uri "http://127.0.0.1:$Port/health" `
                -UseBasicParsing `
                -TimeoutSec 3
            if ($health.StatusCode -ne 200) {
                $lastReason = "Health retornou HTTP $($health.StatusCode)."
            }
            else {
                $frontend = Invoke-WebRequest `
                    -Uri "http://127.0.0.1:$Port/" `
                    -UseBasicParsing `
                    -TimeoutSec 5
                if ($frontend.StatusCode -ne 200 -or $frontend.Content -notmatch 'id="root"') {
                    $lastReason = 'O frontend nao retornou a pagina da aplicacao.'
                }
                elseif ($SkipFullReadiness) {
                    return
                }
                else {
                    $readiness = Invoke-RestMethod `
                        -Uri "http://127.0.0.1:$Port/health/ready" `
                        -TimeoutSec 5
                    if ($readiness.ready -and $readiness.databaseAvailable -and $readiness.plcOnline) {
                        return
                    }
                    $lastReason = "Banco=$($readiness.databaseAvailable); ADS=$($readiness.plcOnline)"
                }
            }
        }
        catch {
            $lastReason = $_.Exception.Message
        }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "A validacao do servico falhou: $lastReason"
}

Assert-Administrator

$packageRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$manifestPath = Join-Path $packageRoot 'deployment-manifest.json'
$packageAppPath = Join-Path $packageRoot 'app'
$packageConfigPath = Join-Path $packageRoot 'config\appsettings.Production.json'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'deployment-manifest.json nao foi encontrado ao lado do instalador.'
}
if (-not (Test-Path -LiteralPath (Join-Path $packageAppPath 'PaperMachine.Historian.Web.exe') -PathType Leaf)) {
    throw 'O executavel publicado nao foi encontrado na pasta app do pacote.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Test-PackageIntegrity -Root $packageRoot -Manifest $manifest

$releasesRoot = Join-Path $InstallRoot 'releases'
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $releasesRoot $manifest.Version))
$applicationConfigPath = Join-Path $DataRoot 'appsettings.Production.json'
$statePath = Join-Path $DataRoot 'deployment-state.json'
$databaseDirectory = Join-Path $DataRoot 'data'
$databasePath = Join-Path $databaseDirectory 'PaperMachineHistorian.db'
$updatesRoot = Join-Path $DataRoot 'updates'
$updateTokenPath = Join-Path $updatesRoot 'github-token.txt'

if (-not $releaseRoot.StartsWith(
        [IO.Path]::GetFullPath($releasesRoot),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'O caminho calculado da release esta fora da pasta de releases.'
}

New-Item -ItemType Directory -Path $releasesRoot -Force | Out-Null
New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $databaseDirectory -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $DataRoot 'backups') -Force | Out-Null
New-Item -ItemType Directory -Path $updatesRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $applicationConfigPath -PathType Leaf)) {
    Copy-Item -LiteralPath $packageConfigPath -Destination $applicationConfigPath
}

$configuration = Get-Content -LiteralPath $applicationConfigPath -Raw | ConvertFrom-Json
$packageConfiguration = Get-Content -LiteralPath $packageConfigPath -Raw | ConvertFrom-Json

# A configuracao persistida pode ter sido criada por uma versao anterior. Sob
# Set-StrictMode, acessar diretamente uma propriedade raiz ausente gera uma
# excecao antes que seja possivel inclui-la, portanto consulte PSObject primeiro.
$historianProperty = $configuration.PSObject.Properties['Historian']
$packageHistorianProperty = $packageConfiguration.PSObject.Properties['Historian']
if ($null -eq $packageHistorianProperty -or $null -eq $packageHistorianProperty.Value) {
    throw "A configuracao do pacote nao possui a secao obrigatoria 'Historian'."
}
if ($null -eq $historianProperty -or $null -eq $historianProperty.Value) {
    $configuration |
        Add-Member -NotePropertyName Historian -NotePropertyValue $packageHistorianProperty.Value -Force
    $historianProperty = $configuration.PSObject.Properties['Historian']
} else {
    foreach ($property in $packageHistorianProperty.Value.PSObject.Properties) {
        if ($null -eq $historianProperty.Value.PSObject.Properties[$property.Name]) {
            $historianProperty.Value |
                Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value
        }
    }

    # Versoes anteriores usavam snapshots JSON a cada 10 segundos. Na primeira
    # atualizacao otimizada, migra apenas esse valor legado conhecido; valores
    # personalizados diferentes de 10 segundos permanecem intactos.
    if ([int]$historianProperty.Value.StatusSnapshotIntervalSeconds -eq 10) {
        $historianProperty.Value.StatusSnapshotIntervalSeconds =
            [int]$packageHistorianProperty.Value.StatusSnapshotIntervalSeconds
    }
}

$updatesProperty = $configuration.PSObject.Properties['Updates']
$packageUpdatesProperty = $packageConfiguration.PSObject.Properties['Updates']
if ($null -eq $packageUpdatesProperty -or $null -eq $packageUpdatesProperty.Value) {
    throw "A configuracao do pacote nao possui a secao obrigatoria 'Updates'."
}
if ($null -eq $updatesProperty -or $null -eq $updatesProperty.Value) {
    $configuration |
        Add-Member -NotePropertyName Updates -NotePropertyValue $packageUpdatesProperty.Value -Force
    $updatesProperty = $configuration.PSObject.Properties['Updates']
}
else {
    foreach ($property in $packageUpdatesProperty.Value.PSObject.Properties) {
        if ($null -eq $updatesProperty.Value.PSObject.Properties[$property.Name]) {
            $updatesProperty.Value |
                Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value
        }
    }
}
if (-not [string]::IsNullOrWhiteSpace($AmsNetId)) {
    $configuration.Ads.AmsNetId = $AmsNetId.Trim()
}
if (-not [string]::IsNullOrWhiteSpace($RemoteIp)) {
    $configuration.Ads.RemoteIp = $RemoteIp.Trim()
}
$configuration.Database.FilePath = $databasePath
$updatesProperty.Value.WorkingDirectory = $updatesRoot
$updatesProperty.Value.TokenFilePath = $updateTokenPath
$updatesProperty.Value.UpdaterTaskName = 'CPNTeckPaperMachineHistorianUpdater'

if ([string]::IsNullOrWhiteSpace([string]$configuration.Ads.AmsNetId)) {
    throw 'AMS Net ID vazio. Informe -AmsNetId ou PAPERHISTORIAN_AMS_NET_ID.'
}
if ([string]::IsNullOrWhiteSpace([string]$configuration.Ads.RemoteIp)) {
    throw 'IP remoto vazio. Informe -RemoteIp ou PAPERHISTORIAN_REMOTE_IP.'
}
Assert-AmsNetId -Value ([string]$configuration.Ads.AmsNetId)
Assert-IpAddress -Value ([string]$configuration.Ads.RemoteIp)
$configuration | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $applicationConfigPath -Encoding UTF8

& icacls.exe $DataRoot `
    '/inheritance:r' `
    '/grant:r' `
    '*S-1-5-18:(OI)(CI)F' `
    '*S-1-5-32-544:(OI)(CI)F' *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'Nao foi possivel proteger a pasta de dados de producao.'
}

if (Test-Path -LiteralPath $releaseRoot) {
    if (-not $Force) {
        throw "Release $($manifest.Version) ja instalada. Use -Force para reinstalar."
    }
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Copy-Item -Path (Join-Path $packageAppPath '*') -Destination $releaseRoot -Recurse -Force

$newExecutablePath = Join-Path $releaseRoot 'PaperMachine.Historian.Web.exe'
$existingState = if (Test-Path -LiteralPath $statePath -PathType Leaf) {
    Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
} else {
    $null
}
$previousVersion = if ($existingState) { [string]$existingState.CurrentVersion } else { $null }
$previousExecutablePath = if ($previousVersion) {
    Join-Path (Join-Path $releasesRoot $previousVersion) 'PaperMachine.Historian.Web.exe'
} else {
    $null
}

$firewallRuleName = "CPNTeckPaperMachineHistorian-HTTP-$HttpPort"
$firewallRule = Get-NetFirewallRule -Name $firewallRuleName -ErrorAction SilentlyContinue
if ($null -eq $firewallRule) {
    New-NetFirewallRule `
        -Name $firewallRuleName `
        -DisplayName "CPNTeck Paper Machine Historian HTTP ($HttpPort)" `
        -Description 'Permite acesso ao Historian somente pela rede local.' `
        -Enabled True `
        -Profile Domain,Private `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort $HttpPort `
        -RemoteAddress LocalSubnet | Out-Null
}
else {
    $firewallRule |
        Set-NetFirewallRule -Enabled True -Profile Domain,Private -Direction Inbound -Action Allow
    $firewallRule |
        Get-NetFirewallPortFilter |
        Set-NetFirewallPortFilter -Protocol TCP -LocalPort $HttpPort
    $firewallRule |
        Get-NetFirewallAddressFilter |
        Set-NetFirewallAddressFilter -RemoteAddress LocalSubnet
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$serviceWasRunning = Stop-ExistingService -Name $ServiceName

try {
    New-DeploymentBackup -Root $DataRoot -PreviousVersion $previousVersion

    if ($null -eq $service) {
        $quotedPath = '"' + $newExecutablePath + '"'
        Invoke-ServiceControl -Arguments @(
            'create', $ServiceName,
            'binPath=', $quotedPath,
            'start=', 'delayed-auto',
            'DisplayName=', $DisplayName
        )
        Invoke-ServiceControl -Arguments @(
            'description', $ServiceName,
            'CPNTeck Paper Machine Historian - coleta ADS somente leitura'
        )
    }
    else {
        Set-ServiceBinaryPath -Name $ServiceName -ExecutablePath $newExecutablePath
    }

    Invoke-ServiceControl -Arguments @(
        'failure', $ServiceName,
        'reset=', '86400',
        'actions=', 'restart/5000/restart/10000/restart/30000'
    )
    Invoke-ServiceControl -Arguments @('failureflag', $ServiceName, '1')

    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    $serviceEnvironment = @(
        'ASPNETCORE_ENVIRONMENT=Production',
        'DOTNET_ENVIRONMENT=Production',
        "ASPNETCORE_URLS=http://${BindAddress}:$HttpPort",
        "PAPER_MACHINE_HISTORIAN_CONFIG=$applicationConfigPath"
    )
    New-ItemProperty `
        -Path $serviceRegistryPath `
        -Name 'Environment' `
        -Value $serviceEnvironment `
        -PropertyType MultiString `
        -Force | Out-Null

    $updaterInstallerPath = Join-Path `
        $packageRoot `
        'Install-PaperMachineHistorianUpdater.ps1'
    if (-not (Test-Path -LiteralPath $updaterInstallerPath -PathType Leaf)) {
        throw 'O instalador da tarefa de atualizacao nao foi encontrado no pacote.'
    }
    & $updaterInstallerPath `
        -DataRoot $DataRoot `
        -InstallRoot $InstallRoot `
        -ServiceName $ServiceName `
        -TaskName ([string]$updatesProperty.Value.UpdaterTaskName) `
        -HttpPort $HttpPort

    Start-AndValidateService `
        -Name $ServiceName `
        -Port $HttpPort `
        -SkipFullReadiness ([bool]$SkipReadinessChecks)

    $state = [ordered]@{
        CurrentVersion = [string]$manifest.Version
        PreviousVersion = $previousVersion
        InstalledAtUtc = [DateTime]::UtcNow.ToString('o')
        PackageCommit = [string]$manifest.GitCommit
        HttpPort = $HttpPort
        AmsNetId = [string]$configuration.Ads.AmsNetId
        RemoteIp = [string]$configuration.Ads.RemoteIp
    }
    $state | ConvertTo-Json |
        Set-Content -LiteralPath $statePath -Encoding UTF8
}
catch {
    Stop-ExistingService -Name $ServiceName | Out-Null
    if ($previousExecutablePath -and (Test-Path -LiteralPath $previousExecutablePath -PathType Leaf)) {
        Set-ServiceBinaryPath -Name $ServiceName -ExecutablePath $previousExecutablePath
        if ($serviceWasRunning) {
            Start-Service -Name $ServiceName
        }
    }
    throw
}

Write-Host ''
Write-Host "[OK] $DisplayName $($manifest.Version) esta em execucao." -ForegroundColor Green
Write-Host "Servico: $ServiceName (inicio automatico atrasado)"
Write-Host "URL local: http://127.0.0.1:$HttpPort"
Write-Host "Rede local: http://<IP-OU-NOME-DESTE-PC>:$HttpPort"
