[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$Repository = 'FabioTCampion/ADF_MP1_TC4024_67',

    [string]$GitHubCliPath,

    [string]$RuntimeIdentifier = 'win-x64',

    [string]$ReleaseNotesPath,

    [switch]$CleanClientDependencies,

    [switch]$SkipTests,

    [switch]$DraftOnly,

    [ValidateSet('Fastest', 'Optimal', 'NoCompression')]
    [string]$CompressionLevel = 'Fastest'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$releaseStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

function Assert-CommandAvailable {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found."
    }
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$Command' failed with exit code $LASTEXITCODE."
    }
}

function Resolve-GitHubCli {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "GitHub CLI was not found at '$resolved'."
        }
        return $resolved
    }

    $command = Get-Command 'gh' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $programFiles = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFiles)
    $standardPath = Join-Path $programFiles 'GitHub CLI\gh.exe'
    if (Test-Path -LiteralPath $standardPath -PathType Leaf) {
        return $standardPath
    }

    throw @"
GitHub CLI was not found. Install it, add it to PATH, or provide
-GitHubCliPath with the full path to gh.exe.
"@
}

function Get-RemoteTagCommit {
    param(
        [Parameter(Mandatory = $true)][string]$RemoteUrl,
        [Parameter(Mandatory = $true)][string]$Tag
    )

    $lines = @(
        & git ls-remote --tags $RemoteUrl "refs/tags/$Tag" "refs/tags/$Tag^{}"
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect remote tag '$Tag'."
    }
    if ($lines.Count -eq 0) {
        return $null
    }

    $peeled = $lines | Where-Object { $_ -match '\^\{\}$' } | Select-Object -First 1
    $selected = if ($peeled) { $peeled } else { $lines | Select-Object -First 1 }
    return (($selected -split '\s+')[0]).Trim()
}

Assert-CommandAvailable -Name 'git'
$ghCommand = Resolve-GitHubCli -RequestedPath $GitHubCliPath

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRootOutput = & git -C $projectRoot rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or -not $repositoryRootOutput) {
    throw 'The project is not inside a Git repository.'
}
$repositoryRoot = [IO.Path]::GetFullPath(
    ($repositoryRootOutput | Select-Object -First 1).Trim())
$branch = (& git -C $repositoryRoot branch --show-current | Select-Object -First 1).Trim()
if (-not $branch) {
    throw 'A release cannot be created from a detached HEAD.'
}

$projectStatus = @(& git -C $projectRoot status --porcelain -- .)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not inspect the application Git status.'
}
if ($projectStatus.Count -gt 0) {
    throw @"
The historian application has uncommitted changes.
Commit the application before creating a release so the manifest can identify
the exact source commit.
"@
}

Invoke-CheckedCommand -Command $ghCommand -Arguments @('auth', 'status')
Invoke-CheckedCommand -Command $ghCommand -Arguments @('auth', 'setup-git')

if ([string]::IsNullOrWhiteSpace($Version)) {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try {
        $latestTagOutput =
            & $ghCommand api "repos/$Repository/releases/latest" --jq '.tag_name' 2>$null
        $latestTagExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($latestTagExitCode -eq 0 -and $latestTagOutput) {
        $latestTag = ($latestTagOutput | Select-Object -First 1).Trim()
        if ($latestTag -notmatch '^v?([0-9]+)\.([0-9]+)\.([0-9]+)$') {
            throw "Latest stable tag is not a simple semantic version: $latestTag"
        }
        $Version = '{0}.{1}.{2}' -f
            [int]$Matches[1],
            [int]$Matches[2],
            ([int]$Matches[3] + 1)
    }
    else {
        $Version = '0.1.0'
    }
    Write-Host "Automatic version selected: $Version" -ForegroundColor Cyan
}

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "Invalid semantic version: $Version"
}

$tag = "v$Version"
$fullCommit = (& git -C $repositoryRoot rev-parse HEAD | Select-Object -First 1).Trim()
$shortCommit = (& git -C $repositoryRoot rev-parse --short HEAD |
    Select-Object -First 1).Trim()
$gitUrl = "https://github.com/$Repository.git"
$packageName = "CPNTeck-PaperMachineHistorian-$Version-$RuntimeIdentifier"
$zipPath = Join-Path $projectRoot "artifacts\$packageName.zip"
$zipHashPath = "$zipPath.sha256"
$manifestPath = Join-Path $projectRoot "artifacts\$packageName\deployment-manifest.json"
$publishScript = Join-Path $PSScriptRoot 'Publish-PaperMachineHistorian.ps1'

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'SilentlyContinue'
try {
    $existingReleaseJson =
        & $ghCommand release view $tag --repo $Repository `
            --json isDraft,isPrerelease,url,assets 2>$null
    $existingReleaseExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}
$existingRelease = if ($existingReleaseExitCode -eq 0 -and $existingReleaseJson) {
    $existingReleaseJson | ConvertFrom-Json
}
else {
    $null
}
if ($existingRelease -and -not $existingRelease.isDraft) {
    throw "Stable release '$tag' already exists: $($existingRelease.url)"
}

$remoteTagCommit = Get-RemoteTagCommit -RemoteUrl $gitUrl -Tag $tag
if ($remoteTagCommit -and $remoteTagCommit -ne $fullCommit) {
    throw "Remote tag '$tag' points to $remoteTagCommit instead of $fullCommit."
}

$localTag = & git -C $repositoryRoot tag --list $tag
if ($localTag) {
    $localTagCommit =
        (& git -C $repositoryRoot rev-list -n 1 $tag | Select-Object -First 1).Trim()
    if ($localTagCommit -ne $fullCommit) {
        throw "Local tag '$tag' points to $localTagCommit instead of $fullCommit."
    }
}
elseif ($remoteTagCommit) {
    Invoke-CheckedCommand -Command 'git' -Arguments @(
        '-C', $repositoryRoot,
        'fetch', $gitUrl,
        "refs/tags/$tag`:refs/tags/$tag"
    )
}

$publishParameters = @{
    Version = $Version
    RuntimeIdentifier = $RuntimeIdentifier
    CompressionLevel = $CompressionLevel
}
if ($CleanClientDependencies) {
    $publishParameters.CleanClientDependencies = $true
}
if ($SkipTests) {
    Write-Warning 'Tests are being skipped. This is not recommended for an official release.'
    $publishParameters.SkipTests = $true
}

Write-Host ''
Write-Host "Building $packageName from $shortCommit..." -ForegroundColor Cyan
& $publishScript @publishParameters

$projectStatusAfterBuild = @(& git -C $projectRoot status --porcelain -- .)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not inspect the application Git status after the build.'
}
if ($projectStatusAfterBuild.Count -gt 0) {
    throw @"
The build changed tracked application files. Review and commit them before
creating the tag. No GitHub Release was published.
"@
}

if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $zipHashPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'The package, checksum or deployment manifest was not generated.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Version -ne $Version) {
    throw "Manifest version '$($manifest.Version)' does not match '$Version'."
}
if ($manifest.GitCommit -ne $shortCommit) {
    throw "Manifest commit '$($manifest.GitCommit)' does not match '$shortCommit'."
}
if ($manifest.GitDirty) {
    throw 'The package manifest reports uncommitted application changes.'
}

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sidecarHash = (
    (Get-Content -LiteralPath $zipHashPath -Raw).Trim() -split '\s+'
)[0].ToLowerInvariant()
if ($zipHash -ne $sidecarHash) {
    throw 'The generated ZIP does not match its SHA-256 sidecar.'
}

if (-not $localTag -and -not $remoteTagCommit) {
    Invoke-CheckedCommand -Command 'git' -Arguments @(
        '-C', $repositoryRoot,
        'tag', '-a', $tag, $fullCommit,
        '-m', "CPNTeck Paper Machine Historian $Version"
    )
}

Write-Host ''
Write-Host 'Pushing branch and tag in one operation...' -ForegroundColor Cyan
Invoke-CheckedCommand -Command 'git' -Arguments @(
    '-C', $repositoryRoot,
    'push', $gitUrl,
    "HEAD:refs/heads/$branch",
    "refs/tags/$tag"
)

$remoteTagCommit = Get-RemoteTagCommit -RemoteUrl $gitUrl -Tag $tag
if ($remoteTagCommit -ne $fullCommit) {
    throw "Remote tag verification failed for '$tag'."
}

$releaseTitle = "CPNTeck Paper Machine Historian $Version"
$releaseNotes = if ($ReleaseNotesPath) {
    $resolvedNotesPath = [IO.Path]::GetFullPath($ReleaseNotesPath)
    if (-not (Test-Path -LiteralPath $resolvedNotesPath -PathType Leaf)) {
        throw "Release notes file was not found: $resolvedNotesPath"
    }
    Get-Content -LiteralPath $resolvedNotesPath -Raw
}
else {
    @"
## Atualizacao $Version

- Pacote autocontido para Windows x64.
- Build otimizado e reproduzivel a partir do commit ``$shortCommit``.
- Manifesto interno e checksum SHA-256 incluidos.
- Testes automatizados executados durante o empacotamento.
"@
}

if ($existingRelease) {
    Write-Host 'Resuming the existing draft release...' -ForegroundColor Cyan
    Invoke-CheckedCommand -Command $ghCommand -Arguments @(
        'release', 'upload', $tag,
        $zipPath, $zipHashPath,
        '--repo', $Repository,
        '--clobber'
    )
    Invoke-CheckedCommand -Command $ghCommand -Arguments @(
        'release', 'edit', $tag,
        '--repo', $Repository,
        '--title', $releaseTitle,
        '--notes', $releaseNotes
    )
}
else {
    Invoke-CheckedCommand -Command $ghCommand -Arguments @(
        'release', 'create', $tag,
        $zipPath, $zipHashPath,
        '--repo', $Repository,
        '--verify-tag',
        '--draft',
        '--title', $releaseTitle,
        '--notes', $releaseNotes
    )
}

if (-not $DraftOnly) {
    Invoke-CheckedCommand -Command $ghCommand -Arguments @(
        'release', 'edit', $tag,
        '--repo', $Repository,
        '--draft=false',
        '--latest'
    )
}

$releaseJson = & $ghCommand api "repos/$Repository/releases/tags/$tag"
if ($LASTEXITCODE -ne 0 -or -not $releaseJson) {
    throw "Could not verify GitHub Release '$tag'."
}
$release = $releaseJson | ConvertFrom-Json
$zipAsset = $release.assets |
    Where-Object { $_.name -eq "$packageName.zip" } |
    Select-Object -First 1
$hashAsset = $release.assets |
    Where-Object { $_.name -eq "$packageName.zip.sha256" } |
    Select-Object -First 1
if (-not $zipAsset -or -not $hashAsset) {
    throw 'GitHub Release does not contain both required assets.'
}
if ($zipAsset.digest -and $zipAsset.digest -ne "sha256:$zipHash") {
    throw "GitHub asset digest does not match the local ZIP: $($zipAsset.digest)"
}

if ($DraftOnly) {
    if (-not $release.draft) {
        throw 'The release should be a draft but GitHub reports it as published.'
    }
}
else {
    if ($release.draft -or $release.prerelease) {
        throw 'The release was not published as a stable release.'
    }
    $latestTag = (
        & $ghCommand api "repos/$Repository/releases/latest" --jq '.tag_name' |
        Select-Object -First 1).Trim()
    if ($latestTag -ne $tag) {
        throw "Latest stable release is '$latestTag', expected '$tag'."
    }
}

$releaseStopwatch.Stop()
Write-Host ''
Write-Host '[OK] Release completed and verified.' -ForegroundColor Green
Write-Host "Version: $Version"
Write-Host "Commit:  $fullCommit"
Write-Host "SHA-256: $zipHash"
Write-Host "URL:     $($release.html_url)"
Write-Host (
    'Total release time: {0:N2} s' -f $releaseStopwatch.Elapsed.TotalSeconds
) -ForegroundColor Green
