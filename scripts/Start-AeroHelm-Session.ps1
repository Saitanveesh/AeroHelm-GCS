# Unified Windows entry point. One startup cloud login feeds the native
# receive-only localhost bridge and all nine touch panels.
param(
  [string]$CloudUrl = 'https://aerohelm-vcn.tailf3ab6a.ts.net',
  [string]$VehicleId = 'AH-HW-001',
  [string]$CameraUrl = 'https://saitanveesh.tailf3ab6a.ts.net/cam',
  [switch]$UseLegacyPythonBridge,
  [switch]$SkipBridge,
  [switch]$ShowNativeToolbar
)
$ErrorActionPreference = 'Stop'
$gcsRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$backendRoot = Join-Path (Split-Path -Parent $gcsRoot) 'AeroHelm'
$touch = Join-Path $PSScriptRoot 'Start-AeroHelm-Touch.ps1'
if ($UseLegacyPythonBridge -and $SkipBridge) {
    throw 'Choose either -UseLegacyPythonBridge or -SkipBridge.'
}
$origin = $null
if (-not [Uri]::TryCreate($CloudUrl,[UriKind]::Absolute,[ref]$origin) -or
    $origin.Scheme -ne 'https' -or $origin.Host -notlike '*.ts.net' -or
    $origin.AbsolutePath -ne '/' -or $origin.UserInfo -ne '' -or
    $origin.Query -ne '' -or $origin.Fragment -ne '') {
    throw 'Cloud URL must be a private Tailscale HTTPS origin.'
}
$camera = $null
if (-not [Uri]::TryCreate($CameraUrl,[UriKind]::Absolute,[ref]$camera) -or
    $camera.Scheme -ne 'https' -or $camera.Host -notlike '*.ts.net' -or
    $camera.UserInfo -ne '' -or $camera.Query -ne '' -or
    $camera.Fragment -ne '') {
    throw 'Camera URL must be a private Tailscale HTTPS address without credentials or query.'
}
if ($VehicleId -notmatch '^[A-Za-z0-9_-]{3,64}$') {
    throw 'Invalid aircraft identifier.'
}
if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close existing Mission Planner windows before starting a new session.'
}
if (-not (Test-Path -LiteralPath $touch -PathType Leaf)) {
    throw 'AeroHelm Touch launcher missing; update GCS checkout.'
}
$listener = @(Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue)
if ($listener.Count -gt 0 -and -not $SkipBridge) {
    if (-not $UseLegacyPythonBridge) {
        throw 'TCP 5760 is in use. Close the old Python bridge before single-login native mode.'
    }
    $ownerId = $listener[0].OwningProcess
    $existing = Get-CimInstance Win32_Process -Filter "ProcessId = $ownerId" -ErrorAction SilentlyContinue
    if (-not $existing -or $existing.CommandLine -notmatch 'aerohelm_tcp_bridge[.]py') {
        throw 'TCP 5760 is occupied by an unrelated process. Refusing to reuse it.'
    }
}
[Environment]::SetEnvironmentVariable('AEROHELM_CLOUD_URL',$origin.GetLeftPart([UriPartial]::Authority),'Process')
[Environment]::SetEnvironmentVariable('AEROHELM_VEHICLE_ID',$VehicleId,'Process')
[Environment]::SetEnvironmentVariable('AEROHELM_CAMERA1_URL',$camera.AbsoluteUri,'Process')
[Environment]::SetEnvironmentVariable('AEROHELM_VISION_AUTO_OPEN','1','Process')

if ($UseLegacyPythonBridge -and $listener.Count -eq 0) {
    $bridge = Join-Path $backendRoot 'scripts\aerohelm_tcp_bridge.py'
    if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) {
        throw 'Python bridge missing; update backend checkout or use the native bridge.'
    }
    $python = Get-Command python.exe -ErrorAction Stop
    Write-Host 'Legacy bridge: requires its own masked password login.'
    $bridgeArgs = @("`"$bridge`"",'--url',$CloudUrl,'--vehicle',$VehicleId)
    Start-Process -FilePath $python.Source -ArgumentList $bridgeArgs -WorkingDirectory $backendRoot | Out-Null
}
$touchArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$touch)
if ($SkipBridge -or $UseLegacyPythonBridge) { $touchArgs += '-NoInternalBridge' }
if ($ShowNativeToolbar) { $touchArgs += '-ShowNativeToolbar' }
Write-Host 'Starting AeroHelm: login first, then TOUCH; private camera URL preconfigured.'
Write-Host 'Native TCP uses 127.0.0.1:5760; outbound GCS commands are discarded.'
& powershell @touchArgs
if ($LASTEXITCODE -ne 0) { throw 'Touch GCS launcher failed.' }
