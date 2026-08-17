[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ConnectorExecutablePath,

    [Parameter(Mandatory = $true)]
    [string]$ResultPath,

    [ValidateRange(1024, 65535)]
    [int]$Port = 15091
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$serviceName = 'CPNTeckProductionConnector'
$startedAt = Get-Date
$testRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ('CPNTeck\ProductionConnectorServiceTest-' + [Guid]::NewGuid().ToString('N'))
$serviceCreated = $false
$result = [ordered]@{
    succeeded = $false
    startedAtUtc = $startedAt.ToUniversalTime().ToString('o')
    completedAtUtc = $null
    serviceName = $serviceName
    port = $Port
    health = $null
    error = $null
    serviceControlManagerEvents = @()
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Execute este teste em uma sessao elevada como Administrador.'
    }
}

function Write-Result {
    $resolvedResultPath = [IO.Path]::GetFullPath($ResultPath)
    $resultDirectory = Split-Path -Parent $resolvedResultPath
    if (-not (Test-Path -LiteralPath $resultDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    }
    $result.completedAtUtc = [DateTime]::UtcNow.ToString('o')
    $json = $result | ConvertTo-Json -Depth 10
    $utf8WithoutBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($resolvedResultPath, $json, $utf8WithoutBom)
}

try {
    Assert-Administrator

    $resolvedExecutable = [IO.Path]::GetFullPath($ConnectorExecutablePath)
    if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
        throw "Executavel do conector nao encontrado: $resolvedExecutable"
    }
    if ($null -ne (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
        throw "O servico $serviceName ja existe; o teste nao alterou o servico existente."
    }

    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $configPath = Join-Path $testRoot 'config.json'
    $config = [ordered]@{
        listenAddress = "127.0.0.1:$Port"
        upstreamUrl = 'https://api.papersystem.com.br/apontamentos/cpnteck/jupia/mp'
        weightUpstreamUrl = 'https://api.papersystem.com.br/apontamentos/cpnteck/jupia/mp/pesagens'
        allowedUpstreamHost = 'api.papersystem.com.br'
        apiKeyHeaderName = 'x-api-key'
        apiKeyFilePath = (Join-Path $testRoot 'erp-api-key.txt')
        clientTokenHeaderName = 'x-cpnteck-connector-token'
        clientTokenFilePath = (Join-Path $testRoot 'historian-token.txt')
        requestTimeoutSeconds = 10
        cacheTtlSeconds = 55
        maximumResponseBytes = 1048576
        maximumRequestBytes = 65536
    }
    $json = $config | ConvertTo-Json -Depth 10
    $utf8WithoutBom = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($configPath, $json, $utf8WithoutBom)

    & $resolvedExecutable --config $configPath --validate-config
    if ($LASTEXITCODE -ne 0) {
        throw "A validacao previa da configuracao falhou com codigo $LASTEXITCODE."
    }

    $binaryPath = '"' + $resolvedExecutable + '" --service --config "' + $configPath + '"'
    New-Service `
        -Name $serviceName `
        -BinaryPathName $binaryPath `
        -DisplayName 'CPNTeck Production Connector Integration Test' `
        -StartupType Manual | Out-Null
    $serviceCreated = $true

    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus(
        [ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try {
            $health = Invoke-RestMethod `
                -Uri "http://127.0.0.1:$Port/health" `
                -TimeoutSec 3
            if ($health.status -eq 'ok') {
                $result.health = $health
                $result.succeeded = $true
                break
            }
        }
        catch {
            $result.error = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    if (-not $result.succeeded) {
        throw "O endpoint /health nao respondeu corretamente: $($result.error)"
    }
}
catch {
    $result.error = $_.Exception.Message
}
finally {
    try {
        $result.serviceControlManagerEvents = @(
            Get-WinEvent -FilterHashtable @{
                LogName = 'System'
                ProviderName = 'Service Control Manager'
                StartTime = $startedAt
            } -ErrorAction SilentlyContinue |
                Where-Object { $_.Message -match [regex]::Escape($serviceName) } |
                Select-Object TimeCreated, Id, LevelDisplayName, Message
        )
    }
    catch {
        # The primary test result remains valid even if event collection fails.
    }

    if ($serviceCreated) {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $serviceName *> $null
    }

    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTestRoot.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }

    Write-Result
}

if (-not $result.succeeded) {
    exit 1
}
