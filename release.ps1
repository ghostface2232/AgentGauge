# Builds the installer and publishes it as a GitHub Release with the gh CLI.
# The tag/name come from <Version> in Gauge.csproj (e.g. 0.1.0 -> v0.1.0), so the
# in-app updater (which reads the same version) can detect and apply it.
#
# Prereqs: GitHub CLI authenticated (`gh auth login`) and Inno Setup 6 installed.
# Usage:   pwsh -File release.ps1 [-Draft] [-Notes "release notes"]
#
# Bump <Version> in Gauge.csproj and commit/push before running.

param(
    [switch]$Draft,
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
} else {
    $ghArgs = @('--title', $tag)
    if ($Draft) { $ghArgs += '--draft' }
    if ($Notes) { $ghArgs += @('--notes', $Notes) } else { $ghArgs += '--generate-notes' }

    Write-Host "==> Creating release $tag..." -ForegroundColor Cyan
    gh release create $tag $installer @ghArgs
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)" }
}

Write-Host "==> Done. Release $tag is published." -ForegroundColor Green
