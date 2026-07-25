[CmdletBinding()]
param(
    [string]$DataRoot = 'C:\ProgramData\CPNTeck\PaperMachineHistorian'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Execute este script em uma janela do PowerShell aberta como Administrador.'
}

$secureToken = Read-Host `
    'Token GitHub somente leitura (Contents: Read) para o repositorio privado' `
    -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
try {
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'O token nao pode estar vazio.'
    }

    $updatesRoot = Join-Path $DataRoot 'updates'
    $tokenPath = Join-Path $updatesRoot 'github-token.txt'
    New-Item -ItemType Directory -Path $updatesRoot -Force | Out-Null
    Set-Content -LiteralPath $tokenPath -Value $token.Trim() -Encoding ASCII -NoNewline

    & icacls.exe $tokenPath `
        '/inheritance:r' `
        '/grant:r' `
        '*S-1-5-18:F' `
        '*S-1-5-32-544:F' *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Nao foi possivel proteger o arquivo do token.'
    }
}
finally {
    if ($pointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    $token = $null
}

Write-Host '[OK] Token GitHub configurado sem grava-lo no appsettings.' -ForegroundColor Green
