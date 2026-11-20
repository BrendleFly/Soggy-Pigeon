# Build the Soggy Pigeon installer.
#
#   1. publish the app self-contained win-x64 with ReadyToRun (no .NET runtime
#      needed on the player's machine)
#   2. stamp the version from Protocol/Proto.cs so the installer, the app and
#      the release feed cannot disagree
#   3. run ISCC over installer/SoggyPigeon.iss
#
# Usage:  pwsh -File installer/make-installer.ps1
# Requires: .NET 8 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- 1. version from the single source of truth -----------------------------
$proto = Get-Content "$root/src/SoggyPigeon.Protocol/Proto.cs" -Raw
$m = [regex]::Match($proto, 'ClientVersion\s*=\s*"([^"]+)"')
if (-not $m.Success) { throw "could not read ClientVersion from Proto.cs" }
$version = $m.Groups[1].Value
Write-Host "version: $version"

# ---- 2. publish -------------------------------------------------------------
$outDir = "$root/publish/app-win"
Write-Host "publishing self-contained win-x64 (ReadyToRun)..."
dotnet publish "$root/src/SoggyPigeon.App/SoggyPigeon.App.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true `
    -o $outDir -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$dll = "$outDir/SoggyPigeon.dll"
if (-not (Test-Path $dll)) { throw "publish produced no SoggyPigeon.dll" }
Write-Host ("published: {0:N1} MB" -f ((Get-ChildItem $outDir -Recurse | Measure-Object Length -Sum).Sum / 1MB))

# ---- 3. compile the installer ----------------------------------------------
$iscc = @(
    "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe",
    "$env:ProgramFiles/Inno Setup 6/ISCC.exe",
    "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found - winget install JRSoftware.InnoSetup" }

Write-Host "compiling installer..."
& $iscc "/DMyAppVersion=$version" "$root/installer/SoggyPigeon.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = "$root/installer-output/SoggyPigeon-Setup-$version.exe"
if (-not (Test-Path $setup)) { throw "installer not produced at $setup" }
Write-Host ""
Write-Host ("installer: {0}  ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))