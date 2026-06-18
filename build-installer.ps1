# Publishes Gauge as a self-contained x64 app, then wraps it in a minimal
# per-user Inno Setup installer (dist\GaugeSetup-win-x64.exe).
#
# Usage:  pwsh -File build-installer.ps1

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'Gauge.csproj'
$rid = 'win-x64'
$appDir = Join-Path $root "dist\app\$rid"
$installerScript = Join-Path $root 'installer\Gauge.iss'
$installerOutput = Join-Path $root 'dist\GaugeSetup-win-x64.exe'

function Find-InnoCompiler {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    throw 'Inno Setup 6 compiler (ISCC.exe) was not found. Install JRSoftware.InnoSetup with winget, then retry.'
}

[xml]$projectXml = Get-Content $project
$version = [string]($projectXml.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Gauge.csproj does not define <Version>.'
}

Write-Host '==> Stopping any running Gauge...' -ForegroundColor Cyan
Get-Process Gauge -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "==> Cleaning $appDir ..." -ForegroundColor Cyan
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }

Write-Host "==> Publishing (self-contained, $rid)..." -ForegroundColor Cyan
# SelfContained + WindowsAppSDKSelfContained come from the csproj; the CopyAppPriToPublish
# target there copies Gauge.pri into the output (publish drops it for unpackaged WinUI).
dotnet publish $project `
    -c Release -r $rid -p:Platform=x64 --self-contained true `
    -o $appDir -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Fail fast if the resource index is missing — without it the app crashes on launch.
if (-not (Test-Path (Join-Path $appDir 'Gauge.pri'))) {
    throw "Gauge.pri is missing from the publish output; the app would crash at startup."
}

$iscc = Find-InnoCompiler
Write-Host "==> Compiling minimal installer with $iscc..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$version" "/DSourceDir=$appDir" $installerScript
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

if (-not (Test-Path $installerOutput)) {
    throw "Installer was not produced: $installerOutput"
}

$sizeMB = [math]::Round((Get-Item $installerOutput).Length / 1MB, 1)
Write-Host "==> Done. $installerOutput ($sizeMB MB)" -ForegroundColor Green
