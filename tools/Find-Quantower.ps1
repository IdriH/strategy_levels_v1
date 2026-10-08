[CmdletBinding()]
param([string]$InstallationRoot, [switch]$PassThru)
$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$candidates = [Collections.Generic.List[string]]::new()
if ($InstallationRoot) { $candidates.Add($InstallationRoot) }
else {
    if ($env:QUANTOWER_ROOT) { $candidates.Add($env:QUANTOWER_ROOT) }
    $propsPath = Join-Path $workspacePath 'Quantower.local.props'
    if (Test-Path -LiteralPath $propsPath) {
        [xml]$props = Get-Content -LiteralPath $propsPath -Raw
        if ($props.Project.PropertyGroup.QuantowerRoot.InnerText) { $candidates.Add($props.Project.PropertyGroup.QuantowerRoot.InnerText) }
    }
    # Probe these locations; a candidate is accepted only after its actual SDK is found.
    foreach ($probe in @('C:\Quantower', (Join-Path $env:ProgramFiles 'Quantower'), (Join-Path $env:LOCALAPPDATA 'Quantower'))) {
        if (Test-Path -LiteralPath $probe) { $candidates.Add($probe) }
    }
}
$discovered = foreach ($candidate in ($candidates | Select-Object -Unique)) {
    $platformPath = Join-Path $candidate 'TradingPlatform'
    if (-not (Test-Path -LiteralPath $platformPath)) { continue }
    foreach ($version in (Get-ChildItem -LiteralPath $platformPath -Directory)) {
        $dllPath = Join-Path $version.FullName 'bin\TradingPlatform.BusinessLayer.dll'
        if (Test-Path -LiteralPath $dllPath) {
            $parsedVersion = [version]'0.0'
            [void][version]::TryParse($version.Name.TrimStart('v'), [ref]$parsedVersion)
            [pscustomobject]@{ Root = (Resolve-Path -LiteralPath $candidate).Path; SdkPath = (Split-Path -Parent $dllPath); Version = $parsedVersion }
        }
    }
}
$selected = $discovered | Sort-Object Version -Descending | Select-Object -First 1
if (-not $selected) { throw 'No installed Quantower SDK found. Supply -InstallationRoot with the actual terminal root.' }
$escapedSdk = [Security.SecurityElement]::Escape($selected.SdkPath)
$escapedRoot = [Security.SecurityElement]::Escape($selected.Root)
$propsText = @"
<Project>
  <PropertyGroup>
    <QuantowerSdkPath Condition="'`$(QuantowerSdkPath)'==''">$escapedSdk</QuantowerSdkPath>
    <QuantowerRoot Condition="'`$(QuantowerRoot)'==''">$escapedRoot</QuantowerRoot>
  </PropertyGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $workspacePath 'Quantower.local.props'), $propsText)
if ($PassThru) { $selected } else { Write-Output "Quantower $($selected.Version): $($selected.SdkPath). Wrote Quantower.local.props." }
