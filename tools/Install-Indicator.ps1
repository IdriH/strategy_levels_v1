[CmdletBinding()]
param([string]$InstallationRoot)
$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installation = & (Join-Path $PSScriptRoot 'Find-Quantower.ps1') -InstallationRoot $InstallationRoot -PassThru
& dotnet build (Join-Path $workspacePath 'candidate_levels_v1_1\candidate_levels_v1_1.csproj') -c Release "-p:QuantowerSdkPath=$($installation.SdkPath)"
if ($LASTEXITCODE -ne 0) { throw 'Indicator build failed; no installation performed.' }
$scriptsRoot = [IO.Path]::GetFullPath((Join-Path $installation.Root 'Settings\Scripts\Indicators'))
$targetPath = [IO.Path]::GetFullPath((Join-Path $scriptsRoot 'candidate_levels_v1_1'))
if (-not $targetPath.StartsWith($scriptsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid installation target.' }
[void](New-Item -ItemType Directory -Path $targetPath -Force)
$buildPath = Join-Path $workspacePath 'candidate_levels_v1_1\bin\Release'
foreach ($name in @('candidate_levels_v1_1.dll', 'candidate_levels_v1_1.pdb', 'candidate_levels_v1_1.deps.json', 'NqCandidateLevels.Core.dll', 'NqCandidateLevels.Core.pdb')) {
    $artifactPath = Join-Path $buildPath $name
    if (Test-Path -LiteralPath $artifactPath) { Copy-Item -LiteralPath $artifactPath -Destination $targetPath -Force }
}
Write-Output "Installed NQZ6 Candidate Levels to $targetPath. Reload/add the indicator in Quantower; inspect its source-status panel."
