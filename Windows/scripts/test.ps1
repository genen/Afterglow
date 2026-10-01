param([string]$AppPath)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet run --project (Join-Path $root 'Afterglow.Tests') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
if (-not $AppPath) { $AppPath = Join-Path $root 'artifacts/win-x64/Afterglow.exe' }
if (-not (Test-Path $AppPath)) { throw "Build first; app not found at $AppPath" }
$AppPath = (Resolve-Path $AppPath).Path
$working = Split-Path $AppPath -Parent
$process = Start-Process -FilePath $AppPath -ArgumentList '--self-test' -WorkingDirectory $working -PassThru -Wait
Get-Content (Join-Path $working 'afterglow-smoke-test.json')
if ($process.ExitCode -ne 0) { throw 'Windows provider smoke tests failed.' }
