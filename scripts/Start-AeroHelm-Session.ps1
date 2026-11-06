# AeroHelm single-login Windows session launcher.
# Native GCS connects to the read-only loopback bridge, never to the Pixhawk.
# No cloud or FC commands; no automatic process termination.
param(
    [string]$CloudUrl = 'https://aerohelm-vcn.tailf3ab6a.ts.net',
    [string]$VehicleId = 'AH-HW-001',
    [string]$CameraUrl = 'https://saitanveesh.tailf3ab6a.ts.net/cam',
    [switch]$UseLegacyPythonBridge,
    [switch]$StopVerifiedLegacyBridge,
    [switch]$SkipBridge,
    [switch]$ShowNativeToolbar
)

$ErrorActionPreference = 'Stop'
$gcsRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$backendRoot = Join-Path (Split-Path -Parent $gcsRoot) 'AeroHelm'
$gcsLauncher = Join-Path $PSScriptRoot 'Start-VihangaVedika-GCS.ps1'
$legacyFile = Join-Path $backendRoot 'scripts\aerohelm_tcp_bridge.py'

if ($UseLegacyPythonBridge -and $SkipBridge) {
    throw 'Choose either -UseLegacyPythonBridge or -SkipBridge.'
}
if ($StopVerifiedLegacyBridge -and ($UseLegacyPythonBridge -or $SkipBridge)) {
    throw '-StopVerifiedLegacyBridge is only for the integrated native bridge.'
}

$origin = $null
if (-not [Uri]::TryCreate($CloudUrl, [UriKind]::Absolute, [ref]$origin) -or
    $origin.Scheme -ne 'https' -or $origin.Host -notlike '*.ts.net' -or
    $origin.AbsolutePath -ne '/' -or $origin.UserInfo -ne '' -or
    $origin.Query -ne '' -or $origin.Fragment -ne '') {
    throw 'Cloud URL must be a private Tailscale HTTPS origin.'
}
$camera = $null
if (-not [Uri]::TryCreate($CameraUrl, [UriKind]::Absolute, [ref]$camera) -or
    $camera.Scheme -ne 'https' -or $camera.Host -notlike '*.ts.net' -or
    $camera.UserInfo -ne '' -or $camera.Query -ne '' -or
    $camera.Fragment -ne '') {
    throw 'Camera URL must be a private Tailscale HTTPS address without credentials or query.'
}
if ($VehicleId -notmatch '^[A-Za-z0-9_-]{3,64}$') {
    throw 'Invalid aircraft identifier.'
}
if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close the existing Mission Planner window before starting a new session.'
}
if (-not (Test-Path -LiteralPath $gcsLauncher -PathType Leaf)) {
    throw 'Vihanga Vedika launcher missing; update the reviewed GCS checkout.'
}

# A wildcard-bound listener also occupies TCP 5760. No firewall changes,
# port stealing, or termination of processes that cannot be verified.
$listeners = @(Get-NetTCPConnection -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue)
if ($listeners.Count -gt 0 -and -not $SkipBridge) {
    $owners = @($listeners | Select-Object -ExpandProperty OwningProcess -Unique)
    if ($owners.Count -ne 1) {
        throw 'Multiple processes own TCP 5760; inspect each listener manually.'
    }
    $listenerPid = [int]$owners[0]
    $existing = Get-CimInstance Win32_Process -Filter "ProcessId = $listenerPid" -ErrorAction SilentlyContinue

    # Identify the exact local script, not merely an arbitrary Python process.
    # This check is intentionally restrictive: if uncertain, stop manually.
    $commandLine = if ($null -ne $existing) { [string]$existing.CommandLine } else { '' }
    $expectedScriptPath = [regex]::Escape($legacyFile)
    $verifiedLegacy = (
        $null -ne $existing -and
        $existing.Name -match '^(python|pythonw)([0-9.]*)[.]exe$' -and
        $commandLine -match $expectedScriptPath
    )
    $publicBinding = @($listeners | Where-Object {
        $_.LocalAddress -notin @('127.0.0.1', '::1')
    }).Count -gt 0

    if ($publicBinding) {
        throw "TCP 5760 has a non-loopback listener (PID $listenerPid). Do not reuse or terminate it automatically."
    }
    if ($UseLegacyPythonBridge) {
        if (-not $verifiedLegacy) {
            throw 'TCP 5760 belongs to an unrelated process. Refusing to reuse it.'
        }
        Write-Host "Using the explicitly selected existing read-only Python bridge (PID $listenerPid)."
    }
    elseif ($StopVerifiedLegacyBridge) {
        if (-not $verifiedLegacy) {
            throw "TCP 5760 belongs to an unverified process (PID $listenerPid). No process stopped."
        }
        # Recheck process identity immediately before a requested stop.
        $check = Get-CimInstance Win32_Process -Filter "ProcessId = $listenerPid" -ErrorAction SilentlyContinue
        if ($null -eq $check -or
            $check.Name -notmatch '^(python|pythonw)([0-9.]*)[.]exe$' -or
            [string]$check.CommandLine -notmatch $expectedScriptPath) {
            throw "The owner of TCP 5760 changed; refusing to stop PID $listenerPid."
        }
        Write-Host "Stopping only the verified legacy AeroHelm bridge PID $listenerPid, as explicitly requested."
        Stop-Process -Id $listenerPid -ErrorAction Stop
        Start-Sleep -Milliseconds 750
        $listeners = @(Get-NetTCPConnection -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue)
        if ($listeners.Count -gt 0) {
            throw 'TCP 5760 is still occupied; inspect listeners manually before launching.'
        }
    }
    else {
        throw "TCP 5760 is occupied (PID $listenerPid). Close the old bridge manually, or explicitly use -StopVerifiedLegacyBridge after confirming its identity."
    }
}

# Set only process-local settings; never write credentials to files or
# alter the existing Pi agent, Oracle container, firewall or FC.
[Environment]::SetEnvironmentVariable('AEROHELM_CLOUD_URL', $origin.GetLeftPart([UriPartial]::Authority), 'Process')
[Environment]::SetEnvironmentVariable('AEROHELM_VEHICLE_ID', $VehicleId, 'Process')
[Environment]::SetEnvironmentVariable('AEROHELM_CAMERA1_URL', $camera.AbsoluteUri, 'Process')
[Environment]::SetEnvironmentVariable('AEROHELM_VISION_AUTO_OPEN', '1', 'Process')

if ($UseLegacyPythonBridge -and $listeners.Count -eq 0) {
    if (-not (Test-Path -LiteralPath $legacyFile -PathType Leaf)) {
        throw 'Legacy Python bridge missing; update backend checkout or use the native bridge.'
    }
    $python = Get-Command python.exe -ErrorAction Stop
    Write-Host 'Legacy bridge selected: separate masked cloud sign-in is required.'
    $bridgeArgs = @('"' + $legacyFile + '"', '--url', $CloudUrl, '--vehicle', $VehicleId)
    Start-Process -FilePath $python.Source -ArgumentList $bridgeArgs -WorkingDirectory $backendRoot | Out-Null
}

$gcsArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $gcsLauncher)
if ($SkipBridge -or $UseLegacyPythonBridge) { $gcsArgs += '-NoInternalBridge' }
if ($ShowNativeToolbar) { $gcsArgs += '-ShowNativeToolbar' }
Write-Host 'Starting Vihanga Vedika: native GCS and its cloud observation toolbar.'
Write-Host 'Native TCP is loopback-only (127.0.0.1:5760); GCS outbound MAVLink is discarded.'
& powershell @gcsArgs
if ($LASTEXITCODE -ne 0) {
    throw 'Vihanga Vedika native launcher failed.'
}
