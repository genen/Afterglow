param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Afterglow.Windows/Afterglow.Windows.csproj'
$publish = Join-Path $root "artifacts/$Runtime"
if (-not (Test-Path (Join-Path $publish 'Afterglow.exe'))) {
    throw "Afterglow.exe was not found in $publish. Run .\scripts\build.ps1 -Stable first."
}
$projectText = [System.IO.File]::ReadAllText($project)
if ($projectText -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { throw 'Version element was not found in Afterglow.Windows.csproj.' }
$version = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$upgrade = if ($Runtime -eq 'win-arm64') { 'C4A91F70-8E22-4B6D-A153-9D7C2E5B1F08' } else { '7B1E0A2C-5D44-4E8A-9C31-2F6A8B0D4E15' }
$platform = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
$publishDir = (Resolve-Path $publish).Path.TrimEnd('\') + '\'
$eula = (Resolve-Path (Join-Path $root 'installer\Eula.rtf')).Path -replace '\\', '/'
$wix = Join-Path $root 'installer/Afterglow.Installer.wixproj'
$out = Join-Path $root 'artifacts'
foreach ($stale in @((Join-Path $root 'installer\obj'), (Join-Path $root 'installer\bin'))) {
    if (Test-Path $stale) { Remove-Item $stale -Recurse -Force }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet build $wix -c Release -t:Rebuild -p:InstallerPlatform=$platform -p:ProductVersion=$version -p:PublishDir=$publishDir -p:UpgradeCode=$upgrade -p:EulaPath=$eula -o $out
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$built = Join-Path $out 'Afterglow.msi'
$named = Join-Path $out "Afterglow-Setup-$Runtime.msi"
if (-not (Test-Path $built)) { throw "WiX did not write $built." }
Move-Item $built $named -Force
$payload = [System.IO.File]::ReadAllText($named)
if ($payload -notlike '*Take Two Partners*') { throw 'The setup was built without the Take Two Partners license. The old license file is still being used.' }
Write-Output "Built $named"
