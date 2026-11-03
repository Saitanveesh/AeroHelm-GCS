# Single Windows entry point. Bridge remains receive-only; aircraft commands blocked.
param(
  [string]$CloudUrl = 'https://aerohelm-vcn.tailf3ab6a.ts.net',
  [string]$VehicleId = 'AH-HW-001',
  [int]$BridgePort = 5760,
  [switch]$SkipBridge
)
$ErrorActionPreference = 'Stop'
$gcsRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$backendRoot = Join-Path (Split-Path -Parent $gcsRoot) 'AeroHelm'
$touch = Join-Path $PSScriptRoot 'Start-AeroHelm-Touch.ps1'
$bridge = Join-Path $backendRoot 'scripts\aerohelm_tcp_bridge.py'
if ($BridgePort -lt 1024 -or $BridgePort -gt 65535) { throw 'Invalid local TCP port' }
$origin = $null
if (-not [Uri]::TryCreate($CloudUrl, [UriKind]::Absolute, [ref]$origin) -or
    $origin.Scheme -ne 'https' -or $origin.Host -notlike '*.ts.net' -or
    $origin.AbsolutePath -ne '/' -or $origin.Query -ne '' -or
    $origin.Fragment -ne '' -or $origin.UserInfo -ne '') {
    throw 'Cloud URL must be a private Tailscale HTTPS origin without credentials, path or query.'
}
if ($VehicleId -notmatch '^[A-Za-z0-9_-]{1,64}$') { throw 'Invalid vehicle ID' }
if (-not (Test-Path -LiteralPath $touch -PathType Leaf)) { throw 'Touch GCS launcher missing' }
if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Mission Planner already running; use the existing GCS or close it.'
}
if (-not $SkipBridge) {
    if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) {
        throw "Backend checkout missing: $backendRoot"
    }
    $listener = @(Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $BridgePort -State Listen -ErrorAction SilentlyContinue)
    if ($listener.Count -gt 0) {
        $ownerId = $listener[0].OwningProcess
        $proc = Get-CimInstance Win32_Process -Filter "ProcessId = $ownerId" -ErrorAction SilentlyContinue
        if (-not $proc -or $proc.CommandLine -notmatch 'aerohelm_tcp_bridge[.]py') {
            throw "Local TCP port $BridgePort is owned by an unknown process; refusing to reuse."
        }
        Write-Host "Reusing existing receive-only bridge on 127.0.0.1:$BridgePort"
    } else {
        $py = Get-Command python.exe -ErrorAction Stop
        Write-Host 'Starting cloud bridge; use its masked operator password dialog.'
        $args = @("`"$bridge`"", '--url', $CloudUrl, '--vehicle', $VehicleId,
                  '--port', [string]$BridgePort)
        Start-Process -FilePath $py.Source -ArgumentList $args -WorkingDirectory $backendRoot | Out-Null
        Write-Host 'Bridge may not listen until successful operator authentication.'
    }
}
Write-Host "Launching AeroHelm GCS. Native TCP is 127.0.0.1:$BridgePort"
Write-Host 'CLOUD login is separate in M1; no passwords or tokens are stored by this launcher.'
& powershell -NoProfile -ExecutionPolicy Bypass -File $touch
if ($LASTEXITCODE -ne 0) { throw 'Touch GCS launcher failed' }
