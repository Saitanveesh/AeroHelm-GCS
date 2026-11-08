# Vihanga native Windows ground-station launcher (AeroHelm runtime compatibility).
# Mission Planner retains all native controls, USB and telemetry-radio paths.
param(
    [switch]$ReadOnlyCloudHUD,
    [switch]$NoInternalBridge,
    [switch]$NoAutoTcp,
    [switch]$ShowNativeToolbar,
    [switch]$LocalDirectBackup,
    [string]$LinkOrigin = '',
    [string]$OracleInstanceOcid = ''
)
$ErrorActionPreference = 'Stop'
$requestedLinkOrigin = if ($LinkOrigin) { $LinkOrigin } else { $env:VIHANGA_LINK_ORIGIN }
$requestedOcid = if ($OracleInstanceOcid) { $OracleInstanceOcid } else {
    $env:VIHANGA_EXPECTED_ORACLE_INSTANCE_OCID
}
if ($requestedOcid) {
    if ($requestedOcid -notmatch '^ocid1[.]instance[.]oc[0-9]+[.][A-Za-z0-9_.-]{24,}$') {
        throw 'Specify the full OCI Compute instance OCID, not an instance name or IP address.'
    }
    if (-not $requestedLinkOrigin) {
        throw 'An Oracle instance OCID requires -LinkOrigin pointing to its dedicated private HTTPS relay.'
    }
    [Environment]::SetEnvironmentVariable(
        'VIHANGA_EXPECTED_ORACLE_INSTANCE_OCID', $requestedOcid, 'Process')
}
if($LocalDirectBackup -and $requestedLinkOrigin) {
    throw 'Dedicated cloud telemetry cannot be selected for an offline direct backup session.'
}
if ($requestedLinkOrigin) {
    $linkUri = $null
    if (-not [Uri]::TryCreate($requestedLinkOrigin,[UriKind]::Absolute,[ref]$linkUri) -or
        $linkUri.Scheme -ne 'https' -or
        -not $linkUri.Host.EndsWith('.ts.net',[StringComparison]::OrdinalIgnoreCase) -or
        $linkUri.UserInfo -or $linkUri.Query -or $linkUri.Fragment -or
        $linkUri.AbsolutePath -ne '/') {
        throw 'LinkOrigin must be an exact private *.ts.net HTTPS origin without credentials or path.'
    }
    [Environment]::SetEnvironmentVariable('VIHANGA_LINK_ORIGIN',
        $linkUri.GetLeftPart([UriPartial]::Authority),'Process')
    # Dedicated relay must not inherit the retired M1 origin.
    [Environment]::SetEnvironmentVariable('AEROHELM_CLOUD_URL',
        $linkUri.GetLeftPart([UriPartial]::Authority),'Process')
}
if($LocalDirectBackup -and (-not $NoInternalBridge -or -not $NoAutoTcp)) {
    throw 'Offline DIRECT requires -NoInternalBridge and -NoAutoTcp; cloud path cannot be silently enabled.'
}
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
    throw 'Close other Mission Planner windows before launching Vihanga.'
}
$branch = (& git -C $root branch --show-current).Trim()
if ($branch -notin @('master','feat/vihanga-phase3-native-toolbar')) {
    throw "Vihanga launcher requires the reviewed master or development branch (found $branch)."
}
# The consolidated branch must NEVER silently launch an old cached
# MissionPlanner.exe or plugin DLL. Use the local manifest produced after a
# successful backed-up native build, not just git branch/source labels.
if ($branch -in @('master','feat/vihanga-phase3-native-toolbar')) {
    $manifestFile = Join-Path $root 'build-logs\aerohelm-consolidated-release.json'
    if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
        throw 'Consolidated runtime not verified. Run scripts\Consolidate-AeroHelmWindows.ps1 -Stage Build -Apply, then -Stage Verify.'
    }
    $release = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    $gitCommit = (& git -C $root rev-parse HEAD).Trim()
    $actualExe = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualDll = (Get-FileHash -LiteralPath $compiled -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($release.schema -ne 'aerohelm.gcs.consolidation.v1' -or
        $release.source_branch -ne $branch -or
        $release.git_commit -ne $gitCommit -or
        $release.executable_sha256 -ne $actualExe -or
        $release.plugin_sha256 -ne $actualDll) {
        throw 'Stale or modified Mission Planner/plugin binaries detected; refusing a misleading latest-version launch.'
    }
}
# The native WebSocket-to-TCP adapter requires an exclusive loopback port.
# Fail BEFORE launching Mission Planner when the old Python bridge is still
# running; the operator can reuse it explicitly via the session launcher
# -UseLegacyPythonBridge. Never kill or overwrite an unknown process.
if (-not $NoInternalBridge) {
    $listeners = @(Get-NetTCPConnection -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue)
    if ($listeners.Count -gt 0) {
        $ownerId = $listeners[0].OwningProcess
        $owner = Get-CimInstance Win32_Process -Filter "ProcessId = $ownerId" -ErrorAction SilentlyContinue
        $isLegacy = $owner -and $owner.CommandLine -match 'aerohelm_tcp_bridge[.]py'
        if ($isLegacy) {
            throw "Existing AeroHelm Python bridge occupies TCP 5760 (PID $ownerId). Close its own terminal with Ctrl+C before launching native mode, or explicitly run Start-AeroHelm-Session.ps1 -UseLegacyPythonBridge to reuse it."
        }
        throw "TCP 5760 occupied by another process (PID $ownerId). Inspect Get-CimInstance Win32_Process -Filter 'ProcessId = $ownerId'; do not terminate an unidentified process."
    }
}
# Cloud login is required for network viewing; offline pilot mode is explicit.
# Native maintenance remains available via --local-maintenance.
[Environment]::SetEnvironmentVariable('AEROHELM_REQUIRE_LOGIN',
    $(if($LocalDirectBackup -or $requestedLinkOrigin){'0'}else{'1'}),'Process')
# The reduced operator toolbar keeps native controls available through their menus.
# Their upstream functionality is preserved for maintenance/recovery.
[Environment]::SetEnvironmentVariable('AEROHELM_REDUCED_TOOLBAR',
    $(if ($ShowNativeToolbar) { '0' } else { '1' }), 'Process')
# In-process single-login receive-only MAVLink bridge by default.
[Environment]::SetEnvironmentVariable('AEROHELM_INTERNAL_TCP_BRIDGE',
    $(if ($NoInternalBridge) { '0' } else { '1' }), 'Process')
# Explicit PATHS selection can use the local receive-only bridge; native transport
# still enforces the normal heartbeat checks.
[Environment]::SetEnvironmentVariable('AEROHELM_AUTO_NATIVE_TCP',
    $(if ($NoAutoTcp -or $NoInternalBridge) { '0' } else { '1' }), 'Process')
if (-not $env:AEROHELM_CLOUD_URL -and -not $LocalDirectBackup) {
    throw 'No cloud origin selected. Use -LinkOrigin for the dedicated relay, or explicitly configure AEROHELM_CLOUD_URL for legacy M1.'
}
if (-not $env:AEROHELM_CAMERA1_URL) {
    [Environment]::SetEnvironmentVariable('AEROHELM_CAMERA1_URL',
        'https://saitanveesh.tailf3ab6a.ts.net/cam', 'Process')
}
if (-not $env:AEROHELM_VISION_AUTO_OPEN) {
    [Environment]::SetEnvironmentVariable('AEROHELM_VISION_AUTO_OPEN','0','Process')
}
# READ-ONLY cloud HUD is mandatory in this release track. Keep the switch
# for compatibility with older operator runbooks, but never permit a
# launch flag to enable remote aircraft commands.
[Environment]::SetEnvironmentVariable('AEROHELM_CLOUD_READONLY','1','Process')
Write-Host 'Starting VIHANGA Ground Station (Mission Planner native interface).'
Write-Host 'Use PATHS to select cloud view or an independent local pilot radio.'
if ($requestedLinkOrigin) {
    Write-Host 'Dedicated telemetry relay selected; PATHS will request separate operator sign-in.'
    if ($requestedOcid) { Write-Host 'Oracle instance OCID binding: required before relay sign-in.' }
}
Write-Host 'Toolbar: native DATA, PLAN, SETUP, CONFIG; Vihanga PATHS, CAMERA, TOOLS.'
if (-not $LocalDirectBackup) {
    Write-Host $(if ($requestedLinkOrigin) { 'Private relay sign-in is requested when PATHS opens.' } else { 'Legacy M1 cloud authentication is required at startup.' })
}
Write-Host 'Native receive-only TCP bridge: 127.0.0.1:5760 (outbound commands discarded).'
if ($LocalDirectBackup) {
    Write-Host 'OFFLINE DIRECT BACKUP: no cloud login, cloud TCP bridge or automatic connection.'
    Write-Host 'Native local COM/telemetry radio may carry real vehicle command authority; pilot must choose deliberately.'
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -ArgumentList '--local-maintenance'
} else {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
}
