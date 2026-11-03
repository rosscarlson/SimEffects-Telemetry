# Builds the installer for the version in GtTelemetry.App.csproj, and with -Publish makes the GitHub release the
# in-app updater looks for (tag vX.Y.Z with the installer attached).
#   powershell -File tools\release.ps1                 # publish\installer\SimEffectsTelemetry-Setup-X.Y.Z.exe
#   powershell -File tools\release.ps1 -Publish -Notes "What changed"
param([switch]$Publish, [string]$Notes = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$version = ([xml](Get-Content src\GtTelemetry.App\GtTelemetry.App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in GtTelemetry.App.csproj" }
Write-Host "SimEffects Telemetry $version"

if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish src\GtTelemetry.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
if ($LASTEXITCODE) { throw "dotnet publish failed" }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 isn't installed: winget install JRSoftware.InnoSetup" }
& $iscc /Q "/DAppVersion=$version" installer\SimEffectsTelemetry.iss
if ($LASTEXITCODE) { throw "ISCC failed" }
$setup = "publish\installer\SimEffectsTelemetry-Setup-$version.exe"
Write-Host "Built $setup"

if ($Publish) {
    if (-not $Notes) { $Notes = "SimEffects Telemetry $version" }
    gh release create "v$version" $setup --title "SimEffects Telemetry $version" --notes $Notes
    if ($LASTEXITCODE) { throw "gh release create failed" }
}
