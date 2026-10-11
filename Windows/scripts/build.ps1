param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$FrameworkDependent,
    [switch]$Stable,
    # Build the version already in the project file without advancing it. Used by CI for beta releases.
    [switch]$KeepVersion
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Afterglow.Windows/Afterglow.Windows.csproj'
$suffix = if ($FrameworkDependent) { "$Runtime-framework-dependent" } else { $Runtime }
$output = Join-Path $root "artifacts/$suffix"
$selfContained = (-not $FrameworkDependent.IsPresent).ToString().ToLowerInvariant()
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$projectText = [System.IO.File]::ReadAllText($project)
if ($projectText -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { throw 'Version element was not found in Afterglow.Windows.csproj.' }
$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3]
# Keep the patch number unless this build advances the minor version.
if (-not $Stable -and -not $KeepVersion) { $minor += 1; $patch = 0 }
$channel = if ($Stable) { 'stable' } else { 'beta' }
$projectText = [regex]::Replace($projectText, '<Version>\d+\.\d+\.\d+</Version>', "<Version>$major.$minor.$patch</Version>", 1)
$projectText = [regex]::Replace($projectText, '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$major.$minor.$patch $channel</InformationalVersion>", 1)
$utf8 = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($project, $projectText, $utf8)
Write-Output "Version $major.$minor.$patch $channel"
dotnet publish $project -c Release -r $Runtime --self-contained $selfContained -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$readme = (Get-Content (Join-Path $root 'README.md') -Raw).Replace('../docs/WINDOWS-PORT.md', 'WINDOWS-PORT.md')
Set-Content -Path (Join-Path $output 'README.md') -Value $readme -Encoding utf8
Copy-Item (Join-Path $root 'VALIDATION.md') (Join-Path $output 'VALIDATION.md') -Force
Copy-Item (Join-Path $root '../docs/WINDOWS-PORT.md') (Join-Path $output 'WINDOWS-PORT.md') -Force
$archive = Join-Path $root "artifacts/Afterglow-$suffix.zip"
Compress-Archive -Path "$output/*" -DestinationPath $archive -Force
Write-Output "Built $archive"
