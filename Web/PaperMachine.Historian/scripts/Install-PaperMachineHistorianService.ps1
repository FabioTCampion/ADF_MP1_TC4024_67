[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\Program Files\CPNTeck\PaperMachineHistorian',

    [string]$DataRoot = 'C:\ProgramData\CPNTeck\PaperMachineHistorian',

    [string]$ServiceName = 'CPNTeckPaperMachineHistorian',

    [string]$DisplayName = 'CPNTeck Paper Machine Historian',

    [string]$ConnectorDataRoot = 'C:\ProgramData\CPNTeck\ProductionConnector',

    [string]$ConnectorServiceName = 'CPNTeckProductionConnector',

    [string]$ConnectorDisplayName = 'CPNTeck Production Connector',

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

function Set-DelayedAutomaticStart {
    param([Parameter(Mandatory = $true)][string]$Name)

    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    New-ItemProperty `
        -Path $serviceRegistryPath `
        -Name 'DelayedAutoStart' `
        -Value 1 `
        -PropertyType DWord `
        -Force | Out-Null
}

function New-ManagedService {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$BinaryPath,
        [Parameter(Mandatory = $true)][string]$ServiceDisplayName
    )

    New-Service `
        -Name $Name `
        -BinaryPathName $BinaryPath `
        -DisplayName $ServiceDisplayName `
        -StartupType Automatic | Out-Null
    Set-DelayedAutomaticStart -Name $Name
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
        [string]$PreviousVersion,
        [string]$ConfigurationContent,
        [string]$ConnectorRoot,
        [string]$ConnectorConfigurationContent
    )

    $databasePath = Join-Path $Root 'data\PaperMachineHistorian.db'
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) {
        return $null
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
    if (-not [string]::IsNullOrWhiteSpace($ConfigurationContent)) {
        $ConfigurationContent |
            Set-Content `
                -LiteralPath (Join-Path $backupRoot 'appsettings.Production.json') `
                -Encoding UTF8
    }
    $keysPath = Join-Path $Root 'keys'
    if (Test-Path -LiteralPath $keysPath -PathType Container) {
        Copy-Item -LiteralPath $keysPath -Destination $backupRoot -Recurse -Force
    }
    $secretsPath = Join-Path $Root 'secrets'
    if (Test-Path -LiteralPath $secretsPath -PathType Container) {
        Copy-Item -LiteralPath $secretsPath -Destination $backupRoot -Recurse -Force
    }
    if (-not [string]::IsNullOrWhiteSpace($ConnectorRoot) -and
        (Test-Path -LiteralPath $ConnectorRoot -PathType Container)) {
        $connectorBackup = Join-Path $backupRoot 'production-connector'
        New-Item -ItemType Directory -Path $connectorBackup -Force | Out-Null
        foreach ($itemName in @('secrets')) {
            $source = Join-Path $ConnectorRoot $itemName
            if (Test-Path -LiteralPath $source) {
                Copy-Item -LiteralPath $source -Destination $connectorBackup -Recurse -Force
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($ConnectorConfigurationContent)) {
            $ConnectorConfigurationContent |
                Set-Content -LiteralPath (Join-Path $connectorBackup 'config.json') -Encoding UTF8
        }
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
    return $backupRoot
}

function Restore-DeploymentBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$BackupPath,
        [string]$ConnectorRoot
    )

    $resolvedBackup = [IO.Path]::GetFullPath($BackupPath)
    $backupsRoot = [IO.Path]::GetFullPath((Join-Path $Root 'backups')).TrimEnd('\') + '\'
    if (-not $resolvedBackup.StartsWith($backupsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup de rollback fora da pasta permitida: $resolvedBackup"
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
    if (-not [string]::IsNullOrWhiteSpace($ConnectorRoot) -and
        (Test-Path -LiteralPath $connectorBackup -PathType Container)) {
        New-Item -ItemType Directory -Path $ConnectorRoot -Force | Out-Null
        Copy-Item -Path (Join-Path $connectorBackup '*') -Destination $ConnectorRoot -Recurse -Force
    }
}

function Set-ServiceBinaryPath {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [string]$Arguments = ''
    )

    $quotedPath = '"' + $ExecutablePath + '"'
    if (-not [string]::IsNullOrWhiteSpace($Arguments)) {
        $quotedPath += ' ' + $Arguments
    }
    $escapedName = $Name.Replace("'", "''")
    $serviceInstance = Get-CimInstance `
        -ClassName Win32_Service `
        -Filter "Name='$escapedName'"
    if ($null -eq $serviceInstance) {
        throw "Servico '$Name' nao encontrado para atualizar o caminho executavel."
    }
    $changeResult = Invoke-CimMethod `
        -InputObject $serviceInstance `
        -MethodName Change `
        -Arguments @{
            PathName = $quotedPath
            StartMode = 'Automatic'
        }
    if ([int]$changeResult.ReturnValue -ne 0) {
        throw "Win32_Service.Change falhou com codigo $($changeResult.ReturnValue) para '$Name'."
    }
    Set-DelayedAutomaticStart -Name $Name
}

function Write-JsonUtf8WithoutBom {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Depth = 10
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $json, $utf8WithoutBom)
}

function Start-AndValidateConnector {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$Port
    )

    Start-Service -Name $Name
    (Get-Service -Name $Name).WaitForStatus(
        [ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $lastReason = 'O endpoint de saude ainda nao respondeu.'
    do {
        try {
            $health = Invoke-RestMethod `
                -Uri "http://127.0.0.1:$Port/health" `
                -TimeoutSec 3
            if ($health.status -eq 'ok') {
                return
            }
        }
        catch {
            $lastReason = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "O conector ERP local nao ficou pronto: $lastReason"
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
$packageConnectorConfigPath = Join-Path $packageRoot 'config\production-connector.config.json'
$packageConnectorExecutablePath = Join-Path `
    $packageAppPath `
    'connector\CPNTeck.ProductionConnector-windows-amd64.exe'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'deployment-manifest.json nao foi encontrado ao lado do instalador.'
}
if (-not (Test-Path -LiteralPath (Join-Path $packageAppPath 'PaperMachine.Historian.Web.exe') -PathType Leaf)) {
    throw 'O executavel publicado nao foi encontrado na pasta app do pacote.'
}
if (-not (Test-Path -LiteralPath $packageConnectorExecutablePath -PathType Leaf)) {
    throw 'O executavel Windows x64 do conector ERP nao foi encontrado no pacote.'
}
if (-not (Test-Path -LiteralPath $packageConnectorConfigPath -PathType Leaf)) {
    throw 'A configuracao do conector ERP nao foi encontrada no pacote.'
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
$connectorConfigPath = Join-Path $ConnectorDataRoot 'config.json'
$connectorSecretsRoot = Join-Path $ConnectorDataRoot 'secrets'
$connectorApiKeyPath = Join-Path $connectorSecretsRoot 'erp-api-key.txt'
$connectorClientTokenPath = Join-Path $connectorSecretsRoot 'historian-token.txt'
$connectorPort = 5091
$connectorConfigurationBeforeUpdateJson = if (
    Test-Path -LiteralPath $connectorConfigPath -PathType Leaf) {
    Get-Content -LiteralPath $connectorConfigPath -Raw
}
else {
    $null
}

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
New-Item -ItemType Directory -Path $ConnectorDataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $connectorSecretsRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $connectorClientTokenPath -PathType Leaf)) {
    $tokenBytes = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($tokenBytes)
    }
    finally {
        $random.Dispose()
    }
    [Convert]::ToBase64String($tokenBytes) |
        Set-Content -LiteralPath $connectorClientTokenPath -Encoding ASCII -NoNewline
    [Array]::Clear($tokenBytes, 0, $tokenBytes.Length)
}

# Migra a credencial do layout anterior apenas quando o novo cofre ainda esta vazio.
$legacyErpKeyPath = Join-Path $DataRoot 'secrets\erp-api-key.txt'
if (-not (Test-Path -LiteralPath $connectorApiKeyPath -PathType Leaf) -and
    (Test-Path -LiteralPath $legacyErpKeyPath -PathType Leaf)) {
    Copy-Item -LiteralPath $legacyErpKeyPath -Destination $connectorApiKeyPath
}

$connectorConfiguration =
    Get-Content -LiteralPath $packageConnectorConfigPath -Raw | ConvertFrom-Json
$connectorConfiguration.apiKeyFilePath = $connectorApiKeyPath
$connectorConfiguration.clientTokenFilePath = $connectorClientTokenPath
$connectorConfiguration.listenAddress = "127.0.0.1:$connectorPort"
Write-JsonUtf8WithoutBom `
    -Value $connectorConfiguration `
    -Path $connectorConfigPath

& $packageConnectorExecutablePath `
    --config $connectorConfigPath `
    --validate-config
if ($LASTEXITCODE -ne 0) {
    throw "O conector ERP rejeitou a configuracao (codigo $LASTEXITCODE)."
}

& icacls.exe $ConnectorDataRoot `
    '/inheritance:r' `
    '/grant:r' `
    '*S-1-5-18:(OI)(CI)F' `
    '*S-1-5-32-544:(OI)(CI)F' *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'Nao foi possivel proteger a pasta do conector ERP.'
}

if (-not (Test-Path -LiteralPath $applicationConfigPath -PathType Leaf)) {
    Copy-Item -LiteralPath $packageConfigPath -Destination $applicationConfigPath
}

$configurationBeforeUpdateJson = Get-Content -LiteralPath $applicationConfigPath -Raw
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

# MappingVersion identifica o contrato de simbolos do PLC e acompanha o
# executavel, nao uma preferencia local. Sempre aplique o valor do pacote para
# que configuracoes preservadas de releases anteriores nao continuem gravando
# snapshots com a versao antiga do mapeamento.
$packageMappingVersion = $packageHistorianProperty.Value.PSObject.Properties['MappingVersion']
if ($null -eq $packageMappingVersion -or
    [string]::IsNullOrWhiteSpace([string]$packageMappingVersion.Value)) {
    throw "A configuracao do pacote nao possui 'Historian:MappingVersion'."
}
$historianProperty.Value.MappingVersion = [string]$packageMappingVersion.Value

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

$productionProperty = $configuration.PSObject.Properties['ProductionIntegration']
$packageProductionProperty = $packageConfiguration.PSObject.Properties['ProductionIntegration']
if ($null -eq $packageProductionProperty -or $null -eq $packageProductionProperty.Value) {
    throw "A configuracao do pacote nao possui a secao obrigatoria 'ProductionIntegration'."
}
if ($null -eq $productionProperty -or $null -eq $productionProperty.Value) {
    $configuration |
        Add-Member `
            -NotePropertyName ProductionIntegration `
            -NotePropertyValue $packageProductionProperty.Value `
            -Force
    $productionProperty = $configuration.PSObject.Properties['ProductionIntegration']
}
else {
    foreach ($property in $packageProductionProperty.Value.PSObject.Properties) {
        if ($null -eq $productionProperty.Value.PSObject.Properties[$property.Name]) {
            $productionProperty.Value |
                Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value
        }
    }
}
$productionProperty.Value.BaseUrl = "http://127.0.0.1:$connectorPort"
$productionProperty.Value.EndpointPath = '/v1/production/current'
$productionProperty.Value.ApiKeyHeaderName = 'x-cpnteck-connector-token'
$productionProperty.Value.ApiKeyFilePath = $connectorClientTokenPath

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

$releaseCopied = $false
if (Test-Path -LiteralPath $releaseRoot) {
    if (-not $Force) {
        throw "Release $($manifest.Version) ja instalada. Use -Force para reinstalar."
    }
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
Copy-Item -Path (Join-Path $packageAppPath '*') -Destination $releaseRoot -Recurse -Force
$releaseCopied = $true

$newExecutablePath = Join-Path $releaseRoot 'PaperMachine.Historian.Web.exe'
$newConnectorExecutablePath = Join-Path `
    $releaseRoot `
    'connector\CPNTeck.ProductionConnector-windows-amd64.exe'
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
$previousConnectorExecutablePath = if ($previousVersion) {
    Join-Path `
        (Join-Path (Join-Path $releasesRoot $previousVersion) 'connector') `
        'CPNTeck.ProductionConnector-windows-amd64.exe'
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
$connectorService = Get-Service -Name $ConnectorServiceName -ErrorAction SilentlyContinue
$connectorServiceWasRunning = Stop-ExistingService -Name $ConnectorServiceName

$rollbackBackupPath = $null
try {
    $rollbackBackupPath = New-DeploymentBackup `
        -Root $DataRoot `
        -PreviousVersion $previousVersion `
        -ConfigurationContent $configurationBeforeUpdateJson `
        -ConnectorRoot $ConnectorDataRoot `
        -ConnectorConfigurationContent $connectorConfigurationBeforeUpdateJson

    $connectorArguments = '--service --config "' + $connectorConfigPath + '"'
    if ($null -eq $connectorService) {
        $connectorBinaryPath = '"' + $newConnectorExecutablePath + '" ' + $connectorArguments
        New-ManagedService `
            -Name $ConnectorServiceName `
            -BinaryPath $connectorBinaryPath `
            -ServiceDisplayName $ConnectorDisplayName
        Invoke-ServiceControl -Arguments @(
            'description', $ConnectorServiceName,
            'CPNTeck local TLS 1.3 connector for read-only production data'
        )
    }
    else {
        Set-ServiceBinaryPath `
            -Name $ConnectorServiceName `
            -ExecutablePath $newConnectorExecutablePath `
            -Arguments $connectorArguments
    }
    Invoke-ServiceControl -Arguments @(
        'failure', $ConnectorServiceName,
        'reset=', '86400',
        'actions=', 'restart/5000/restart/10000/restart/30000'
    )
    Invoke-ServiceControl -Arguments @('failureflag', $ConnectorServiceName, '1')

    if ($null -eq $service) {
        $quotedPath = '"' + $newExecutablePath + '"'
        New-ManagedService `
            -Name $ServiceName `
            -BinaryPath $quotedPath `
            -ServiceDisplayName $DisplayName
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
    Invoke-ServiceControl -Arguments @(
        'config', $ServiceName,
        'depend=', $ConnectorServiceName
    )

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

    Start-AndValidateConnector -Name $ConnectorServiceName -Port $connectorPort

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
        RollbackBackupPath = $rollbackBackupPath
        ConnectorServiceName = $ConnectorServiceName
        ConnectorDataRoot = $ConnectorDataRoot
    }
    $state | ConvertTo-Json |
        Set-Content -LiteralPath $statePath -Encoding UTF8
}
catch {
    Stop-ExistingService -Name $ServiceName | Out-Null
    Stop-ExistingService -Name $ConnectorServiceName | Out-Null
    if ($previousExecutablePath -and (Test-Path -LiteralPath $previousExecutablePath -PathType Leaf)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$rollbackBackupPath)) {
            Restore-DeploymentBackup `
                -Root $DataRoot `
                -BackupPath $rollbackBackupPath `
                -ConnectorRoot $ConnectorDataRoot
        }
        Set-ServiceBinaryPath -Name $ServiceName -ExecutablePath $previousExecutablePath
        if ($previousConnectorExecutablePath -and
            (Test-Path -LiteralPath $previousConnectorExecutablePath -PathType Leaf)) {
            Set-ServiceBinaryPath `
                -Name $ConnectorServiceName `
                -ExecutablePath $previousConnectorExecutablePath `
                -Arguments ('--service --config "' + $connectorConfigPath + '"')
            if ($connectorServiceWasRunning) {
                Start-Service -Name $ConnectorServiceName
            }
        }
        else {
            Invoke-ServiceControl -Arguments @(
                'config', $ServiceName,
                'depend=', '/'
            )
            if ($null -eq $connectorService -and
                $null -ne (Get-Service -Name $ConnectorServiceName -ErrorAction SilentlyContinue)) {
                Invoke-ServiceControl -Arguments @('delete', $ConnectorServiceName)
            }
        }
        if ($serviceWasRunning) {
            Start-Service -Name $ServiceName
        }
    }
    if ($releaseCopied -and
        -not [string]::Equals(
            [string]$manifest.Version,
            [string]$previousVersion,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $releaseRoot -PathType Container)) {
        $resolvedReleaseRoot = [IO.Path]::GetFullPath($releaseRoot)
        $resolvedReleasesPrefix =
            [IO.Path]::GetFullPath($releasesRoot).TrimEnd('\') + '\'
        if ($resolvedReleaseRoot.StartsWith(
                $resolvedReleasesPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedReleaseRoot -Recurse -Force
        }
    }
    throw
}

Write-Host ''
Write-Host "[OK] $DisplayName $($manifest.Version) esta em execucao." -ForegroundColor Green
Write-Host "Servico: $ServiceName (inicio automatico atrasado)"
Write-Host "Conector ERP: $ConnectorServiceName (somente 127.0.0.1:$connectorPort)"
Write-Host "URL local: http://127.0.0.1:$HttpPort"
Write-Host "Rede local: http://<IP-OU-NOME-DESTE-PC>:$HttpPort"
