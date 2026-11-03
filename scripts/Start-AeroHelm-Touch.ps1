# AeroHelm 9-tile Windows touch-console launcher.
# Mission Planner retains all native controls, USB and telemetry-radio paths.
param(
    [switch]$ManualTouch,
    [switch]$ReadOnlyCloudHUD,
    [switch]$NoInternalBridge,
    [switch]$ShowNativeToolbar
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
# Requiring operator login before native Mission Planner and TOUCH is the
# default here regardless of product branding. Offline maintenance remains
# available via the explicit native --local-maintenance recovery option.
[Environment]::SetEnvironmentVariable('AEROHELM_REQUIRE_LOGIN','1','Process')
# Hide the nonessential HELP and SIMULATION toolbar blocks in touch mode.
# Their upstream functionality is preserved for maintenance/recovery.
[Environment]::SetEnvironmentVariable('AEROHELM_REDUCED_TOOLBAR',
    $(if ($ShowNativeToolbar) { '0' } else { '1' }), 'Process')
# In-process single-login receive-only MAVLink bridge by default.
[Environment]::SetEnvironmentVariable('AEROHELM_INTERNAL_TCP_BRIDGE',
    $(if ($NoInternalBridge) { '0' } else { '1' }), 'Process')
if (-not $env:AEROHELM_CLOUD_URL) {
    [Environment]::SetEnvironmentVariable('AEROHELM_CLOUD_URL',
        'https://aerohelm-vcn.tailf3ab6a.ts.net', 'Process')
}
if (-not $env:AEROHELM_CAMERA1_URL) {
    [Environment]::SetEnvironmentVariable('AEROHELM_CAMERA1_URL',
        'https://saitanveesh.tailf3ab6a.ts.net/cam', 'Process')
}
if (-not $env:AEROHELM_VISION_AUTO_OPEN) {
    [Environment]::SetEnvironmentVariable('AEROHELM_VISION_AUTO_OPEN','1','Process')
}
# READ-ONLY cloud HUD is mandatory in this release track. Keep the switch
# for compatibility with older operator runbooks, but never permit a
# launch flag to enable remote aircraft commands.
[Environment]::SetEnvironmentVariable('AEROHELM_CLOUD_READONLY','1','Process')
Write-Host 'Starting native Windows AeroHelm GCS touch panel.'
Write-Host 'FLIGHT and PLAN navigate within Mission Planner. The other tiles open AeroHelm modules.'
Write-Host 'Cloud command authority does not change. No automatic arming or mission execution.'
Write-Host 'Authenticate once at startup. The touch panel opens after successful login.'
Write-Host 'Native TCP uses 127.0.0.1:5760 and discards all outbound commands.'
if ($ManualTouch) { Write-Host 'Manual mode: click TOUCH in Mission Planner.' }
Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
