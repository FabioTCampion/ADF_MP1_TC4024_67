[CmdletBinding()]
param(
    [string]$ConnectorDataRoot = 'C:\ProgramData\CPNTeck\ProductionConnector',

    [string]$ConnectorServiceName = 'CPNTeckProductionConnector',

    [string]$HistorianDataRoot = 'C:\ProgramData\CPNTeck\PaperMachineHistorian',

    [string]$HistorianServiceName = 'CPNTeckPaperMachineHistorian',

    [string]$InstallRoot = 'C:\Program Files\CPNTeck\PaperMachineHistorian',

    [string]$ConnectorExecutablePath,

    [string]$ResultPath,

    [switch]$ValidateOnly,

    [switch]$SkipServiceRestart
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

function Resolve-ConnectorExecutable {
    if (-not [string]::IsNullOrWhiteSpace($ConnectorExecutablePath)) {
        $candidate = [IO.Path]::GetFullPath($ConnectorExecutablePath)
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
        throw "Executavel do conector nao encontrado: $candidate"
    }
    $packageCandidate = Join-Path `
        $PSScriptRoot `
        'app\connector\CPNTeck.ProductionConnector-windows-amd64.exe'
    if (Test-Path -LiteralPath $packageCandidate -PathType Leaf) {
        return $packageCandidate
    }
    $statePath = Join-Path $HistorianDataRoot 'deployment-state.json'
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $version = [string]$state.CurrentVersion
        $installedCandidate = Join-Path `
            (Join-Path (Join-Path $InstallRoot 'releases') $version) `
            'connector\CPNTeck.ProductionConnector-windows-amd64.exe'
        if (Test-Path -LiteralPath $installedCandidate -PathType Leaf) {
            return $installedCandidate
        }
    }
    throw 'Executavel do CPNTeck Production Connector nao encontrado.'
}

if (-not $ValidateOnly) {
    Assert-Administrator
}

$connectorExecutable = Resolve-ConnectorExecutable
$connectorConfigPath = Join-Path $ConnectorDataRoot 'config.json'
$connectorSecretsRoot = Join-Path $ConnectorDataRoot 'secrets'
$apiKeyPath = Join-Path $connectorSecretsRoot 'erp-api-key.txt'
$clientTokenPath = Join-Path $connectorSecretsRoot 'historian-token.txt'
$historianConfigPath = Join-Path $HistorianDataRoot 'appsettings.Production.json'

if (-not (Test-Path -LiteralPath $connectorConfigPath -PathType Leaf)) {
    throw "Configuracao do conector nao encontrada: $connectorConfigPath"
}
if (-not $ValidateOnly -and
    -not (Test-Path -LiteralPath $historianConfigPath -PathType Leaf)) {
    throw "Configuracao do Historian nao encontrada: $historianConfigPath"
}

New-Item -ItemType Directory -Path $connectorSecretsRoot -Force | Out-Null
if (-not $ValidateOnly) {
    & icacls.exe $ConnectorDataRoot `
        '/inheritance:r' `
        '/grant:r' `
        '*S-1-5-18:(OI)(CI)F' `
        '*S-1-5-32-544:(OI)(CI)F' *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Nao foi possivel proteger a pasta de segredos do conector.'
    }
}

$secureKey = Read-Host 'Chave somente leitura atual da API ERP' -AsSecureString
$pointer = [IntPtr]::Zero
$temporaryKeyPath = Join-Path `
    $connectorSecretsRoot `
    ('erp-api-key.{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
$temporaryHistorianConfigPath = $null
$validationResult = [ordered]@{
    Success = $false
    TestedAtUtc = [DateTime]::UtcNow.ToString('o')
    ProductionMapId = $null
    ProductionOrder = $null
    IsProducing = $null
    ItemCount = $null
}
try {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    $apiKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ([string]::IsNullOrWhiteSpace($apiKey)) {
        throw 'A chave da API ERP nao pode estar vazia.'
    }
    Set-Content `
        -LiteralPath $temporaryKeyPath `
        -Value $apiKey.Trim() `
        -Encoding ASCII `
        -NoNewline

    Write-Host 'Validando TLS 1.3, certificado, autenticacao e contrato JSON ...' `
        -ForegroundColor Cyan
    $probeOutput = @(& $connectorExecutable `
        --test-upstream `
        --config $connectorConfigPath `
        --api-key-file $temporaryKeyPath 2>&1)
    $probeExitCode = $LASTEXITCODE
    $probeOutput | ForEach-Object { Write-Host $_ }
    if ($probeExitCode -ne 0) {
        throw 'A validacao falhou; a chave instalada anteriormente foi preservada.'
    }
    $summary = $probeOutput[-1] | ConvertFrom-Json
    $validationResult.Success = $true
    $validationResult.ProductionMapId = [string]$summary.productionMapId
    $validationResult.ProductionOrder = [string]$summary.productionOrder
    $validationResult.IsProducing = [bool]$summary.isProducing
    $validationResult.ItemCount = [int]$summary.itemCount

    if ($ValidateOnly) {
        Write-Host '[OK] Chave atual validada; nenhuma configuracao foi alterada.' `
            -ForegroundColor Green
        return
    }

    if (-not (Test-Path -LiteralPath $clientTokenPath -PathType Leaf)) {
        throw 'Token local Historian-conector nao encontrado. Execute primeiro o instalador da aplicacao.'
    }
    $configuration =
        Get-Content -LiteralPath $historianConfigPath -Raw | ConvertFrom-Json
    $integration = $configuration.PSObject.Properties['ProductionIntegration']
    if ($null -eq $integration -or $null -eq $integration.Value) {
        throw 'A configuracao do Historian nao possui a secao ProductionIntegration.'
    }
    $integration.Value.Enabled = $true
    $integration.Value.Provider = 'PaperSystem'
    $integration.Value.BaseUrl = 'http://127.0.0.1:5091'
    $integration.Value.EndpointPath = '/v1/production/current'
    $integration.Value.PollIntervalSeconds = 60
    $integration.Value.ApiKeyHeaderName = 'x-cpnteck-connector-token'
    $integration.Value.ApiKeyFilePath = $clientTokenPath

    $temporaryHistorianConfigPath = Join-Path `
        (Split-Path -Parent $historianConfigPath) `
        ('appsettings.Production.{0}.tmp' -f [Guid]::NewGuid().ToString('N'))
    $configuration | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $temporaryHistorianConfigPath -Encoding UTF8

    Move-Item -LiteralPath $temporaryKeyPath -Destination $apiKeyPath -Force
    $temporaryKeyPath = $null
    Move-Item `
        -LiteralPath $temporaryHistorianConfigPath `
        -Destination $historianConfigPath `
        -Force
    $temporaryHistorianConfigPath = $null
}
finally {
    if ($null -ne $temporaryKeyPath -and
        (Test-Path -LiteralPath $temporaryKeyPath -PathType Leaf)) {
        Remove-Item -LiteralPath $temporaryKeyPath -Force
    }
    if ($null -ne $temporaryHistorianConfigPath -and
        (Test-Path -LiteralPath $temporaryHistorianConfigPath -PathType Leaf)) {
        Remove-Item -LiteralPath $temporaryHistorianConfigPath -Force
    }
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    $apiKey = $null
    if (-not [string]::IsNullOrWhiteSpace($ResultPath)) {
        $resolvedResultPath = [IO.Path]::GetFullPath($ResultPath)
        $validationResult | ConvertTo-Json -Depth 3 |
            Set-Content -LiteralPath $resolvedResultPath -Encoding UTF8
    }
}

if (-not $SkipServiceRestart) {
    foreach ($serviceName in @($ConnectorServiceName, $HistorianServiceName)) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -ne $service) {
            Restart-Service -Name $serviceName -Force
            $service.WaitForStatus(
                [ServiceProcess.ServiceControllerStatus]::Running,
                [TimeSpan]::FromSeconds(30))
        }
    }
}

Write-Host '[OK] Integracao ERP habilitada por conector local; a chave nao foi gravada no appsettings.' `
    -ForegroundColor Green
