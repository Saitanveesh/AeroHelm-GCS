# Emergency-compatible OFFLINE PILOT BACKUP entry point.
# Requires local physical USB/telemetry-radio hardware already configured.
# This preserves native Mission Planner and never remotely arms the aircraft.
param(
    [switch]$AcknowledgePilotAuthority,
    [switch]$ShowNativeToolbar
)
$ErrorActionPreference='Stop'
if (-not $AcknowledgePilotAuthority) {
    throw 'Native USB/radio may transmit REAL flight commands. Rerun with -AcknowledgePilotAuthority only as an authorized on-site pilot.'
}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close any other Mission Planner session before opening pilot DIRECT backup.'
}
$latest=Join-Path $PSScriptRoot 'Consolidate-AeroHelmWindows.ps1'
# Enforces source commit + EXE/DLL fingerprints from the signed-off
# locally rebuilt release; do not run random older Mission Planner binaries.
& powershell -NoProfile -ExecutionPolicy Bypass -File $latest -Stage Verify
if ($LASTEXITCODE -ne 0) {
    throw 'Verified AeroHelm desktop build required for offline pilot backup.'
}
$touch=Join-Path $PSScriptRoot 'Start-VihangaGCS.ps1'
$flags=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$touch,
         '-NoInternalBridge','-NoAutoTcp','-LocalDirectBackup')
if ($ShowNativeToolbar) {$flags+='-ShowNativeToolbar'}
Write-Host 'OFFLINE DIRECT: Oracle/KDC login not required, no cloud WebSocket receiver.'
Write-Host 'Pilot must select the correct independent COM telemetry-radio/USB port in Mission Planner.'
& powershell @flags
if ($LASTEXITCODE -ne 0) { throw 'Offline direct backup desktop launcher failed.' }
