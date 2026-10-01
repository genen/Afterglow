param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Afterglow.Windows/Afterglow.Windows.csproj'
$suffix = if ($FrameworkDependent) { "$Runtime-framework-dependent" } else { $Runtime }
$output = Join-Path $root "artifacts/$suffix"
$selfContained = (-not $FrameworkDependent.IsPresent).ToString().ToLowerInvariant()
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
dotnet publish $project -c Release -r $Runtime --self-contained $selfContained -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$readme = (Get-Content (Join-Path $root 'README.md') -Raw).Replace('../docs/WINDOWS-PORT.md', 'WINDOWS-PORT.md')
Set-Content -Path (Join-Path $output 'README.md') -Value $readme -Encoding utf8
Copy-Item (Join-Path $root 'VALIDATION.md') (Join-Path $output 'VALIDATION.md') -Force
Copy-Item (Join-Path $root '../docs/WINDOWS-PORT.md') (Join-Path $output 'WINDOWS-PORT.md') -Force
$archive = Join-Path $root "artifacts/Afterglow-$suffix.zip"
Compress-Archive -Path "$output/*" -DestinationPath $archive -Force
Write-Output "Built $archive"
