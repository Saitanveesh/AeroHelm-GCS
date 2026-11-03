# AeroHelm 9-tile Windows touch-console launcher.
# Mission Planner retains all native controls, USB and telemetry-radio paths.
param(
    [switch]$ManualTouch,
    [switch]$ReadOnlyCloudHUD
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $root 'bin\Release\net461\MissionPlanner.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'Mission Planner build not found; run scripts\Build-Integrated-AeroHelm.ps1 first.'
}
$pluginDir = Join-Path (Split-Path -Parent $exe) 'plugins'
$compiled = Join-Path $pluginDir 'AeroHelm.Cloud.Plugin.Validation.dll'
$legacy = Join-Path $pluginDir 'AeroHelmCloud.cs'
if (-not (Test-Path -LiteralPath $compiled -PathType Leaf)) {
    throw 'Compiled AeroHelm plugin is missing. Build and install with scripts\Build-Integrated-AeroHelm.ps1 first.'
}
if (Test-Path -LiteralPath $legacy -PathType Leaf) {
    throw 'Duplicate AeroHelm source plugin detected alongside DLL. Stop and use the transactional installer; do not launch.'
}
if ((Get-Item -LiteralPath $compiled).Length -lt 1024) {
    throw 'Compiled AeroHelm plugin looks invalid (too small). Do not launch.'
}
if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close other Mission Planner windows before launching AeroHelm Touch.'
}
$branch = (& git -C $root branch --show-current).Trim()
if ($branch -notin @('feat/aerohelm-touch-console', 'feat/aerohelm-windows-integration', 'feat/aerohelm-windows-pages-v1', 'feat/aerohelm-windows-readiness-v2', 'feat/aerohelm-windows-release-candidate', 'feat/aerohelm-combined-preflight')) {
    throw "Touch launcher requires the reviewed Windows integration branch (found $branch)."
}
if ($ManualTouch) {
    [Environment]::SetEnvironmentVariable('AEROHELM_TOUCH_STARTUP','0','Process')
} else {
    [Environment]::SetEnvironmentVariable('AEROHELM_TOUCH_STARTUP','1','Process')
}
# READ-ONLY cloud HUD is mandatory in this release track. Keep the switch
# for compatibility with older operator runbooks, but never permit a
# launch flag to enable remote aircraft commands.
[Environment]::SetEnvironmentVariable('AEROHELM_CLOUD_READONLY','1','Process')
Write-Host 'Starting native Windows AeroHelm GCS touch panel.'
Write-Host 'FLIGHT and PLAN navigate within Mission Planner. The other tiles open AeroHelm modules.'
Write-Host 'Cloud command authority does not change. No automatic arming or mission execution.'
Write-Host 'If startup login is active, authenticate first; then the touch panel will open.'
if ($ManualTouch) { Write-Host 'Manual mode: click TOUCH in Mission Planner.' }
Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
