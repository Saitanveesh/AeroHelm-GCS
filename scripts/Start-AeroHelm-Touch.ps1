# Compatibility entry point retained for existing AeroHelm runbooks.
# The nine-tile TOUCH interface was retired; this opens native Mission Planner.
param(
    [switch]$ManualTouch,
    [switch]$ReadOnlyCloudHUD,
    [switch]$NoInternalBridge,
    [switch]$NoAutoTcp,
    [switch]$ShowNativeToolbar,
    [switch]$LocalDirectBackup
)
$ErrorActionPreference='Stop'
if($ManualTouch) {
    Write-Warning 'TOUCH was retired. Native Flight Data, Plan, PATHS and MORE remain available.'
}
& (Join-Path $PSScriptRoot 'Start-VihangaGCS.ps1') `
    -ReadOnlyCloudHUD:$ReadOnlyCloudHUD `
    -NoInternalBridge:$NoInternalBridge `
    -NoAutoTcp:$NoAutoTcp `
    -ShowNativeToolbar:$ShowNativeToolbar `
    -LocalDirectBackup:$LocalDirectBackup
