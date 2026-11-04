# AeroHelm Windows source-contract checks. Fast, offline, no aircraft and no
# credentials required. These are NOT replacements for MSBuild or hardware tests.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Read-Source([string]$path) {
    $full = Join-Path $root $path
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Required Windows source missing: $path"
    }
    return [IO.File]::ReadAllText($full)
}
function Require-Text([string]$content, [string]$needle, [string]$reason) {
    if (-not $content.Contains($needle)) { throw $reason }
}
$touch = Read-Source 'plugins/AeroHelmTouchPanel.cs'
$pages = Read-Source 'plugins/AeroHelmReadOnlyPages.cs'
$twinLab = Read-Source 'plugins/AeroHelmTwinLabWindow.cs'
$fleetLab = Read-Source 'plugins/AeroHelmFleetLabWindow.cs'
$workspace = Read-Source 'plugins/AeroHelmWorkspace.cs'
$operations = Read-Source 'plugins/AeroHelmOperations.cs'
$project = Read-Source 'Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj'
$mainProject = Read-Source 'MissionPlanner.csproj'
$cloud = Read-Source 'plugins/AeroHelmCloud.cs'
$startupGate = Read-Source 'AeroHelmStartupGate.cs'
$installer = Read-Source 'scripts/Install-AeroHelmCloud.ps1'
$rollback = Read-Source 'scripts/Restore-AeroHelmPlugin.ps1'
$roundtrip = Read-Source 'scripts/Test-AeroHelmPluginRoundtrip.ps1'
$preflight = Read-Source 'scripts/Test-AeroHelmPrivatePreflight.ps1'
$launcher = Read-Source 'scripts/Start-AeroHelm-Touch.ps1'
$session = Read-Source 'scripts/Start-AeroHelm-Session.ps1'
$nativeBridge = Read-Source 'plugins/AeroHelmReceiveOnlyBridge.cs'
$bridgeContracts = Read-Source 'Tools/AeroHelmMavlinkBridgeTests/Program.cs'
$mainNative = Read-Source 'MainV2.cs'
$integratedBuild = Read-Source 'scripts/Build-Integrated-AeroHelm.ps1'

foreach ($row in @(
    '"FLIGHT", "PLAN", "VISION"',
    '"OPS", "SECURITY", "FLEET"',
    '"LINK", "ANALYTICS", "SETTINGS"'
)) {
    Require-Text $touch $row "Wrong touch 3x3 tile labels/order: $row"
}
Require-Text $touch 'tiles.GrowStyle = TableLayoutPanelGrowStyle.FixedSize' 'Touch grid must not wrap.'
Require-Text $touch 'AeroHelmIconPainter.Make(name, 72)' 'Tile art must use deterministic native line icons.'
foreach ($module in @('FLEET','SECURITY','ANALYTICS')) {
    Require-Text $touch ('new AeroHelmReadOnlyPage("'+$module+'")') "Touch page missing: $module"
}
Require-Text $pages 'api/v3/research/fleet' 'Research fleet service must be read-only GET.'
Require-Text $pages 'api/v3/research/evidence-integrity' 'Security evidence verification route missing.'
Require-Text $pages 'api/v3/vehicles/' 'Per-aircraft digital-shadow route missing.'
Require-Text $pages 'digital-shadow' 'ANALYTICS digital shadow is not wired.'
Require-Text $pages 'cyber-events' 'SECURITY CyberShield is not wired.'
Require-Text $pages 'vision-events' 'Camera inference metadata viewer missing.'
Require-Text $pages 'RenderResearch' 'Research pages must disclose experimental status.'
Require-Text $pages 'new AeroHelmTwinLabWindow()' 'Twin calibration lab must be available under ANALYTICS.'
Require-Text $project 'AeroHelmTwinLabWindow.cs' 'The twin lab must be part of the native plugin.'
Require-Text $twinLab 'api/v3/research/twin-lab/evaluate' 'Twin lab route is not configured.'
Require-Text $twinLab 'No calibration is applied to Pixhawk.' 'Twin model must never be deployed to aircraft.'
Require-Text $twinLab 'ShowDialog(this)' 'Operator must consent before uploading reference data.'
Require-Text $pages 'new AeroHelmFleetLabWindow()' 'FLEET must link its advisory scheduler.'
Require-Text $project 'AeroHelmFleetLabWindow.cs' 'Native plugin must compile fleet lab.'
Require-Text $fleetLab 'api/v3/fleet/schedule/draft' 'Fleet planner route not wired.'
Require-Text $fleetLab 'No command or upload will occur.' 'Fleet scheduler cannot imply hardware dispatch.'
Require-Text $pages 'No physical flight-control authority.' 'Research pages must not suggest flight authorization.'
Require-Text $touch 'MainV2.instance' 'Mission Planner must remain the native root.'
Require-Text $touch 'Keys.Escape' 'Touch view must retain Escape-to-close.'
Require-Text $touch 'RefreshCloudStatusAsync' 'HOME authenticated link status is missing.'
Require-Text $touch 'api/v1/mavlink/health' 'HOME status must use read-only MAVLink health.'
Require-Text $touch 'CLOUD / STALE OR DISCONNECTED' 'HOME must report stale aircraft status.'
Require-Text $touch 'CLOUD / UNAVAILABLE' 'HOME needs a fail-closed disconnected state.'
Require-Text $cloud 'AEROHELM_TOUCH_STARTUP' 'Windows touch startup opt-in is missing.'
Require-Text $cloud 'AEROHELM_REDUCED_TOOLBAR' 'Native HELP/Simulation recovery path is missing.'
Require-Text $launcher 'AEROHELM_REQUIRE_LOGIN' 'Touch startup must require cloud login.'
Require-Text $startupGate 'AeroHelm.Cloud.Plugin.Validation.dll' 'Direct AeroHelm GCS launches must require login.'
Require-Text $startupGate '--local-maintenance' 'Offline maintenance override must remain explicit.'
Require-Text $startupGate 'url.Host.EndsWith(".ts.net"' 'Startup sign-in must only use private HTTPS.'
Require-Text $launcher 'AEROHELM_REDUCED_TOOLBAR' 'Touch mode should hide HELP and SIMULATION by default.'
Require-Text $launcher 'AEROHELM_INTERNAL_TCP_BRIDGE' 'Native single-login adapter opt-in is missing.'
Require-Text $session 'AEROHELM_CAMERA1_URL' 'Camera URL should be configured once from the session.'
Require-Text $session 'UseLegacyPythonBridge' 'Legacy external bridge fallback is missing.'
Require-Text $cloud 'ActiveNativeBridge.Start()' 'Native receive-only bridge startup missing.'
Require-Text $nativeBridge 'IPAddress.Loopback, 5760' 'Native TCP listener must remain loopback only.'
Require-Text $nativeBridge 'DrainGcsAsync' 'GCS-originating bytes must be discarded.'
Require-Text $nativeBridge 'ValidFrame' 'Inbound MAVLink frame envelope must be validated.'
Require-Text $nativeBridge 'TryReadAttitude' 'Real FC roll/pitch decode must be tested.'
Require-Text $nativeBridge 'HB ' 'Heartbeat freshness must be explicit.'
Require-Text $bridgeContracts 'AeroHelmReceiveOnlyBridge.TryReadAttitude' 'Native parser regression tests missing.'
Require-Text $integratedBuild 'AeroHelmMavlinkBridgeTests.csproj' 'Native bridge tests must run before plugin installation.'
Require-Text $integratedBuild 'feat/gcs-research-cybershield-twin-ui' 'Build launcher must accept the reviewed research branch.'
Require-Text $launcher 'feat/gcs-research-cybershield-twin-ui' 'Touch launcher must accept the research branch.'
Require-Text $launcher 'AEROHELM_AUTO_NATIVE_TCP' 'FLIGHT should connect its local bridge without prompting.'
Require-Text $mainNative 'Host = "127.0.0.1", Port = "5760"' 'Native Mission Planner TCP should bind local bridge without prompting.'
Require-Text $mainNative 'getparams = false;' 'Any read-only cloud TCP caller must skip parameter requests.'
Require-Text $mainNative 'if (aeroHelmReadOnlyTcp)' 'TCP connection itself must enforce read-only routing policy.'
Require-Text $mainNative 'string.Equals(portname, "TCP", StringComparison.OrdinalIgnoreCase)' 'Read-only policy must not alter native serial/radio parameter downloads.'
Require-Text $touch 'main.doConnect(MainV2.comPort,"TCP","57600"' 'Touch FLIGHT must reach native verified MAVLink receive path.'
Require-Text $cloud 'AeroHelmIconPainter.Make(label,20)' 'AeroHelm toolbar needs per-module vector icons.'
Require-Text $cloud 'main.MenuSimulation.Visible = false' 'Hide Simulation toolbar in default GCS profile.'
Require-Text $cloud 'main.MenuHelp.Visible = false' 'Hide Help toolbar in default GCS profile.'
Require-Text $nativeBridge 'ws.ReceiveAsync' 'Cloud receiver must be authenticated and read only.'
if ($nativeBridge.Contains('ws.SendAsync') -or $nativeBridge.Contains('ws.Send(')) {
    throw 'Native GCS must not send packets over the cloud WebSocket.'
}
Require-Text $launcher 'AEROHELM_CLOUD_READONLY' 'Windows launcher must set read-only cloud environment.'
Require-Text $launcher 'Duplicate AeroHelm source plugin' 'Launcher must refuse a duplicate runtime plugin.'
Require-Text $installer 'previous_dll_sha256' 'Plugin installer must record previous DLL checksum.'
Require-Text $installer 'Copy-Item -LiteralPath $target -Destination' 'Installer must back up the installed DLL.'
Require-Text $installer 'previous_source_sha256' 'Installer must preserve legacy dynamic source plugin.'
Require-Text $installer 'Plugin installation failed; attempting to restore' 'Installer must roll back failed installation.'
Require-Text $rollback 'Assert-PreviousHash' 'Rollback must verify backup hashes before restoration.'
Require-Text $rollback 'if (-not $Apply)' 'Restoration must require an explicit -Apply switch.'
Require-Text $installer 'AEROHELM_TEST_FAIL_AFTER_COPY' 'Installer must support triple-gated CI failure injection.'
Require-Text $installer "`$env:GITHUB_ACTIONS -eq 'true'" 'Installer fault injection requires Actions flag.'
Require-Text $roundtrip 'Expected injected installation failure was not observed.' 'CI must exercise automatic failure rollback.'
Require-Text $roundtrip 'Automatic installer error rollback did not restore previous DLL bytes.' 'CI must prove original DLL was restored.'
Require-Text $roundtrip 'Automatic installer error rollback did not restore previous C# plugin.' 'CI must prove old dynamic source was restored.'
Require-Text $preflight "Method='Get'" 'Private preflight must make observations via GET.'
Require-Text $preflight "Method='Post'" 'Private preflight authentication contract missing.'
Require-Text $preflight 'api/v1/auth/login' 'Login must be the only authenticated mutation.'
Require-Text $preflight 'api/v2/security/posture' 'Private preflight must reject command-enabled policy.'
Require-Text $preflight 'pi_fresh_link_reported' 'Private preflight must report link freshness.'
Require-Text $preflight 'parameter_cache_verified_complete' 'M2 cache must be inspected.'
Require-Text $preflight 'physical_flight_authorized=$false' 'Private preflight can never grant flight permissions.'
Require-Text $preflight 'aerohelm-private-preflight.json' 'Preflight report must be written locally.'

foreach ($route in @(
    'api/v2/fleet',
    'api/v2/vehicles/',
    'api/v2/security/posture',
    'api/v2/security/audit',
    'api/v2/vehicles/'
)) {
    Require-Text $pages $route "Missing authenticated read-only backend contract: $route"
}
Require-Text $pages 'OperationsApi.Request("GET",route)' 'Page requests must use GET only.'
Require-Text $pages 'case "discover": route = "api/v2/fleet"' 'Analytics must discover registered aircraft.'
Require-Text $pages 'availableAircraft.Items.Count == 1' 'Auto-selection must require one unambiguous registered aircraft.'
Require-Text $pages 'if (operation == "discover")' 'Analytics discovery needs an independent response view.'
if ($pages -match 'OperationsApi.Request\("(POST|PUT|PATCH|DELETE)"') {
    throw 'Read-only pages unexpectedly contain an API mutation.'
}
Require-Text $pages 'table.Rows.Clear();' 'Failed-link UI must clear stale observations.'
Require-Text $pages 'DateTime.UtcNow' 'Fetch time label is required.'
Require-Text $workspace 'vision-url-2' 'Second private camera configuration missing.'
Require-Text $workspace 'AEROHELM_CAMERA1_URL' 'VISION must prefill the known private Camera 1 URL.'
Require-Text $workspace 'AEROHELM_VISION_AUTO_OPEN' 'VISION must open the saved camera directly.'
Require-Text $workspace 'AeroHelmWorkspaceSettings.CheckedPrivateUrl(url.Text)' 'Camera URL validation missing.'
Require-Text $workspace 'refreshTimer.Interval = 15000' 'LINK bounded polling setting missing.'
Require-Text $workspace 'refreshTimer.Stop()' 'LINK watcher must stop on close.'
# Windows PowerShell 5.1 decodes BOM-less .ps1 files as ANSI. Construct the
# Unicode em dash at runtime so this source test works on PS 5.1 and PS 7+.
$opsPolicyTitle = 'CAPABILITIES ' + [char]0x2014 + ' READ ONLY'
Require-Text $operations $opsPolicyTitle 'OPS policy page is missing.'
Require-Text $operations 'Api("GET","api/v1/operations/capabilities")' 'OPS policy must be a read-only GET.'
foreach ($excluded in @('obj3','Tools')) {
    Require-Text $mainProject ('<Compile Remove="'+$excluded+'\**" />') "Main GCS must exclude $excluded from SDK default compilation."
}
Require-Text $project 'AeroHelmReadOnlyPages.cs' 'Native plugin project must compile new pages.'
Write-Host 'PASS: Windows nine-tile wiring and read-only page contract checks.' -ForegroundColor Green
Write-Host 'Remaining gates: MSBuild, native plugin load, Windows touch UX, Pi/Pixhawk bench tests.'
