# Builds the installer and uploads it to a GitHub Release with the gh CLI. The tag/name come
# from <Version> in Gauge.csproj (e.g. 0.1.0 -> v0.1.0), so the in-app updater (which reads the
# same version) can detect and apply it.
#
# The default is a DRAFT. Clients install a published release by themselves on their next
# launch, so publishing is the point of no return and has to be asked for by name.
#
# Prereqs: GitHub CLI authenticated (`gh auth login`) and Inno Setup 6 installed.
# Usage:   pwsh -File release.ps1 [-Publish] [-Notes "release notes"]
#
# Bump <Version> in Gauge.csproj and commit/push to main before running.

param(
    [switch]$Publish,
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'Gauge.csproj'
$installer = Join-Path $root 'dist\GaugeSetup-win-x64.exe'

[xml]$projectXml = Get-Content $project
$version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Gauge.csproj does not define <Version>.' }
$tag = "v$version"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) was not found. Install it and run "gh auth login", then retry.'
}

# The asset must be built from exactly what the tag will point at. gh creates a missing tag on
# the remote's default branch, so an unpushed bump would ship local code under an old commit;
# the release workflow's tag check then fails too, and the Mac asset never gets uploaded.
Push-Location $root
try {
    $dirty = git status --porcelain --untracked-files=no
    if ($dirty) { throw 'The working tree has uncommitted changes. Commit or stash them first.' }

    git fetch origin main --quiet
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed ($LASTEXITCODE)" }
    $head = (git rev-parse HEAD).Trim()
    $remoteMain = (git rev-parse origin/main).Trim()
    if ($head -ne $remoteMain) { throw "HEAD ($head) is not origin/main ($remoteMain). Push or check out main first." }

    $remoteTag = git ls-remote --tags origin "refs/tags/$tag^{}" "refs/tags/$tag"
    if ($remoteTag) {
        $tagCommit = (($remoteTag | Select-Object -Last 1) -split '\s+')[0]
        if ($tagCommit -ne $head) { throw "Tag $tag already points at $tagCommit, not HEAD ($head)." }
    }
} finally {
    Pop-Location
}

Write-Host "==> Running tests..." -ForegroundColor Cyan
dotnet test (Join-Path $root 'Gauge.Tests\Gauge.Tests.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed ($LASTEXITCODE)" }

Write-Host "==> Building installer for $tag..." -ForegroundColor Cyan
& (Join-Path $root 'build-installer.ps1')
if ($LASTEXITCODE -ne 0) { throw "build-installer.ps1 failed ($LASTEXITCODE)" }
if (-not (Test-Path $installer)) { throw "Installer not found: $installer" }

# Reuse an existing DRAFT for this tag (re-upload the asset) or create a new release. A
# published release is never touched: replacing its installer would ship new code under an
# old version no client is ever offered, and change the asset digest clients already hold.
$isDraft = gh release view $tag --json isDraft --jq '.isDraft' 2> $null
$releaseExists = ($LASTEXITCODE -eq 0)

if ($releaseExists -and $isDraft -ne 'true') {
    throw "Release $tag is already published. Bump <Version> in Gauge.csproj for a new release."
}

if ($releaseExists) {
    Write-Host "==> Draft $tag already exists; replacing the installer asset..." -ForegroundColor Cyan
    gh release upload $tag $installer --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed ($LASTEXITCODE)" }
    if ($Publish) {
        gh release edit $tag --draft=false
        if ($LASTEXITCODE -ne 0) { throw "gh release edit failed ($LASTEXITCODE)" }
    }
} else {
    $ghArgs = @('--title', $tag, '--target', $head)
    if (-not $Publish) { $ghArgs += '--draft' }
    if ($Notes) { $ghArgs += @('--notes', $Notes) } else { $ghArgs += '--generate-notes' }

    Write-Host "==> Creating release $tag..." -ForegroundColor Cyan
    gh release create $tag $installer @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)" }
}

$state = if ($Publish) { 'published' } else { 'a draft; publish it with -Publish or on GitHub' }
Write-Host "==> Done. Release $tag is $state." -ForegroundColor Green
