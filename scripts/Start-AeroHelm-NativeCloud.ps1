# AeroHelm Mission Planner M1/M2 cloud connection launcher.
# Run ONLY on Windows and ONLY after its private Python TCP bridge starts.
param(
    [switch]$CachedParameters,
    [int]$Port = 5760
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$exe = Join-Path $root "bin\Release\net461\MissionPlanner.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "GCS executable missing: first run Build-Integrated-AeroHelm.ps1."
}
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw "Close all Mission Planner windows before changing cloud mode."
}
if ($Port -ne 5760) {
    Write-Warning "Mission Planner TCP host/port must match your Python bridge port $Port."
}
if ($CachedParameters) {
    throw '-CachedParameters is not supported in the unified cloud receive-only release. Use the M2 verified offline cache separately; no cloud TCP parameter request is permitted.'
}
$env:AEROHELM_CLOUD_READONLY = "1"
Write-Host 'AeroHelm receive-only Flight Data: native parameter sync is suppressed.' -ForegroundColor Cyan
Start-Process -FilePath $exe
Write-Host "In AeroHelm: choose TCP -> host 127.0.0.1 -> port $Port."
Write-Host "The CLOUD registry's view session is not a native MAVLink TCP connection."
