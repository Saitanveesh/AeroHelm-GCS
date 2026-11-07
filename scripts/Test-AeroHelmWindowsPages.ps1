# AeroHelm Windows source-contract checks. Fast, offline, no aircraft and no
# credentials required. These are NOT replacements for MSBuild or hardware tests.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Parse ACTUAL PowerShell launcher files before checking string contracts or
# starting the expensive native build. An earlier copy/paste source corruption
# produced three duplicated environment blocks and unterminated regex quotes;
# static contains() checks missed it completely.
$productionScripts = @(
    'scripts/Start-AeroHelm-Session.ps1',
    'scripts/Start-AeroHelm-DirectBackup.ps1',
    'scripts/Start-AeroHelm-Touch.ps1',
    'scripts/Start-VihangaGCS.ps1',
    'scripts/Test-VihangaPassiveBench.ps1',
    'scripts/Test-VihangaCertification.ps1',
    'scripts/Update-AeroHelmLatest.ps1',
    'scripts/Repair-AeroHelmWindows.ps1',
    'scripts/Consolidate-AeroHelmWindows.ps1',
    'scripts/Cleanup-AeroHelmGenerated.ps1',
    'scripts/Build-Integrated-AeroHelm.ps1'
)
foreach ($relative in $productionScripts) {
    $candidate = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Required PowerShell launcher not found: $relative"
    }
    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $candidate, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
        $first = $parseErrors[0]
        throw "PowerShell SYNTAX FAILED in $relative at line $($first.Extent.StartLineNumber): $($first.Message)"
    }
}
# Structural guard against accidentally pasting a second launcher block into
# the middle of a quoted string. ParseFile alone cannot catch every validly
# quoted but semantically duplicated source.
$sessionText = [IO.File]::ReadAllText((Join-Path $root 'scripts/Start-AeroHelm-Session.ps1'))
if ([regex]::Matches($sessionText, 'AEROHELM_VISION_AUTO_OPEN').Count -ne 1) {
    throw 'Session launcher must configure AEROHELM_VISION_AUTO_OPEN exactly once.'
}

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
$shared = Read-Source 'plugins/VihangaSharedUi.cs'
$paths = Read-Source 'plugins/AeroHelmLinkSelector.cs'
$pilotDirect = Read-Source 'plugins/AeroHelmPilotDirectWindow.cs'
$observedAttitude = Read-Source 'plugins/AeroHelmCloudAttitudeWindow.cs'
# Separate PILOT DIRECT radio module must remain a human-authorized native link.
Require-Text $pilotDirect 'SerialPort.GetPortNames()' 'PILOT DIRECT must enumerate Windows physical COM ports.'
Require-Text $pilotDirect 'MessageBoxButtons.YesNo' 'PILOT DIRECT requires explicit on-site authorization.'
Require-Text $pilotDirect 'main.doConnect(MainV2.comPort,port,baud,true,true)' 'PILOT DIRECT must use native Mission Planner transport.'

$directBackup = Read-Source 'scripts/Start-AeroHelm-DirectBackup.ps1'
$pages = Read-Source 'plugins/AeroHelmReadOnlyPages.cs'
$twinLab = Read-Source 'plugins/AeroHelmTwinLabWindow.cs'
$latestLauncher = Read-Source 'scripts/Update-AeroHelmLatest.ps1'
$fleetLab = Read-Source 'plugins/AeroHelmFleetLabWindow.cs'
$workspace = Read-Source 'plugins/AeroHelmWorkspace.cs'
$operations = Read-Source 'plugins/AeroHelmOperations.cs'
$project = Read-Source 'Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj'
$mainProject = Read-Source 'MissionPlanner.csproj'
# Prevent the SDK's recursive Compile glob from including archived runtime
# plugin .cs files, the source of the CS0101/CS0111 Windows build failures.
Require-Text $mainProject 'Compile Remove="build-logs\**"' 'Historical runtime backups would be compiled as duplicate source classes.'
Require-Text $mainProject 'EmbeddedResource Remove="build-logs\**"' 'Historical backups must be excluded from embedded resource items.'
Require-Text $mainProject 'None Remove="build-logs\**"' 'Historical backups must be excluded from SDK None items.'
$cloud = Read-Source 'plugins/AeroHelmCloud.cs'
$startupGate = Read-Source 'AeroHelmStartupGate.cs'
Require-Text $startupGate 'AEROHELM_KERBEROS_UI_ENABLED' 'Enterprise SSO must be opt-in.'
Require-Text $startupGate 'AEROHELM_KERBEROS_SSO_ORIGIN' 'Windows Negotiate must be pinned to explicit private origin.'
Require-Text $startupGate 'cache.Add(url,"Negotiate"' 'Windows SSO must use Negotiate, not Basic auth.'
$nativeProgram = Read-Source 'Program.cs'
$nativeSplash = Read-Source 'Splash.Designer.cs'
$installer = Read-Source 'scripts/Install-AeroHelmCloud.ps1'
$rollback = Read-Source 'scripts/Restore-AeroHelmPlugin.ps1'
$roundtrip = Read-Source 'scripts/Test-AeroHelmPluginRoundtrip.ps1'
$preflight = Read-Source 'scripts/Test-AeroHelmPrivatePreflight.ps1'
$launcher = Read-Source 'scripts/Start-VihangaGCS.ps1'
$session = Read-Source 'scripts/Start-AeroHelm-Session.ps1'
$nativeBridge = Read-Source 'plugins/AeroHelmReceiveOnlyBridge.cs'
$cloudQuality = Read-Source 'plugins/AeroHelmLinkQuality.cs'
$bridgeHarnessProject = Read-Source 'Tools/AeroHelmMavlinkBridgeTests/AeroHelmMavlinkBridgeTests.csproj'
$bridgeContracts = Read-Source 'Tools/AeroHelmMavlinkBridgeTests/Program.cs'
$mainNative = Read-Source 'MainV2.cs'
$integratedBuild = Read-Source 'scripts/Build-Integrated-AeroHelm.ps1'
$consolidation = Read-Source 'scripts/Consolidate-AeroHelmWindows.ps1'
$moduleAudit = Read-Source 'scripts/Audit-AeroHelmModules.ps1'
$generatedCleanup = Read-Source 'scripts/Cleanup-AeroHelmGenerated.ps1'
$legacyNativeLauncher = Read-Source 'scripts/Start-AeroHelm-NativeCloud.ps1'

foreach ($item in @('CLOUD','PATHS','SURVEY','VISION','LINK','OPS','INTEL','MORE')) {
    $needle = if ($item -eq 'CLOUD') {'new ToolStripButton("CLOUD")'}
        elseif ($item -eq 'MORE') {'new ToolStripDropDownButton("MORE")'}
        else {'NewToolbarButton("'+$item+'"'}
    Require-Text $cloud $needle "Native toolbar entry missing: $item"
}
foreach ($item in @('FLEET','SECURITY','ANALYTICS')) {
    Require-Text $cloud ('new AeroHelmReadOnlyPage("'+$item+'")') "MORE workspace missing: $item"
}
Require-Text $cloud 'new AeroHelmIntelWindow("PARAMETERS")' 'Parameter inspection must have direct navigation.'
Require-Text $cloud 'new VihangaSettingsWindow()' 'Settings must have direct navigation.'
if (Test-Path -LiteralPath (Join-Path $root 'plugins/AeroHelmTouchPanel.cs')) {
    throw 'Obsolete TOUCH panel must not ship.'
}
Require-Text $project 'VihangaSharedUi.cs' 'Shared icon and settings components must compile.'
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
Require-Text $twinLab 'internal sealed class AeroHelmPhysicsLabWindow' 'Six-DOF physics-lab form is missing.'
Require-Text $twinLab 'api/v3/research/physics-lab/evaluate' 'Six-DOF physics API must be wired under V3.'
Require-Text $pages 'new AeroHelmPhysicsLabWindow()' 'ANALYTICS must expose the offline physics lab.'
Require-Text $pages 'Cloud capabilities' 'Operator must be able to check backend V3 deployment state.'
Require-Text $pages 'Kerberos integration: NOT PROVISIONED' 'Do not claim Kerberos exists without a KDC.'
Require-Text $pages 'Kerberos readiness' 'SECURITY must expose actual KDC/SPNEGO deployment readiness.'
Require-Text $pages 'table.Rows.Add("Kerberos KDC/realm", "NOT PROVISIONED")' 'Kerberos must fail closed until infrastructure exists.'
Require-Text $workspace 'api/v1/mavlink/attitude' 'LINK must fetch authenticated real attitude preview from M1.'
Require-Text $workspace 'ProbeDuplexAsync("ping")' 'LINK must expose authenticated Pi diagnostic ping.'
Require-Text $workspace 'ProbeDuplexAsync("uplink_status")' 'LINK must expose bounded Pi uplink counters.'
Require-Text $workspace 'RequireAuthenticatedOrigin()' 'Duplex must never forward a JWT to arbitrary LINK origin.'
Require-Text $workspace 'api/v1/network/requests' 'Duplex requires authenticated request/ack contracts.'
Require-Text $workspace 'Flight commands remain blocked.' 'Diagnostic path must not claim aircraft control.'

Require-Text $latestLauncher 'merge --ff-only' 'Latest GCS updater must not force-reset tracked code.'
Require-Text $latestLauncher 'StopVerifiedLegacyBridge' 'Port recovery must require explicit verified-bridge opt-in.'
Require-Text $twinLab 'No calibration is applied to Pixhawk.' 'Twin model must never be deployed to aircraft.'
Require-Text $twinLab 'ShowDialog(this)' 'Operator must consent before uploading reference data.'
Require-Text $pages 'new AeroHelmFleetLabWindow()' 'FLEET must link its advisory scheduler.'
Require-Text $project 'AeroHelmFleetLabWindow.cs' 'Native plugin must compile fleet lab.'
Require-Text $fleetLab 'api/v3/fleet/schedule/draft' 'Fleet planner route not wired.'
Require-Text $fleetLab 'No command or upload will occur.' 'Fleet scheduler cannot imply hardware dispatch.'
Require-Text $pages 'No physical flight-control authority.' 'Research pages must not suggest flight authorization.'
Require-Text $cloud 'MainV2.instance' 'Native Mission Planner must remain the root.'
Require-Text $cloud 'CheckCloudLinkAsync()' 'PATHS status monitor missing.'
Require-Text $cloud '"GET","api/v1/mavlink/health"' 'Live heartbeat and attitude check missing.'
Require-Text $cloud 'CLOUD HEARTBEAT/ATTITUDE MISSING' 'Stale HUD evidence must fail closed.'
if ($cloud.Contains('AEROHELM_TOUCH_STARTUP') -or $cloud.Contains('AeroHelmTouchPanel')) {
    throw 'Obsolete TOUCH startup is still present.'
}
Require-Text $cloud 'AEROHELM_REDUCED_TOOLBAR' 'Native HELP/Simulation recovery path is missing.'
Require-Text $launcher 'AEROHELM_REQUIRE_LOGIN' 'GCS startup must require cloud login.'
Require-Text $startupGate 'AeroHelm.Cloud.Plugin.Validation.dll' 'Direct GCS launches must require login.'
Require-Text $startupGate 'title.Equals("Vihanga GCS"' 'Vihanga-branded launches must retain startup authentication.'
Require-Text $startupGate 'title.Equals("AeroHelm GCS"' 'Legacy branded launches must retain startup authentication.'
Require-Text $startupGate '--local-maintenance' 'Offline maintenance override must remain explicit.'
Require-Text $nativeSplash 'this.TopMost = true;' 'Splash layering contract changed; recheck login modal behavior.'
Require-Text $nativeProgram 'Splash.Hide();' 'Splash must be hidden during cloud modal authentication.'
Require-Text $nativeProgram 'Splash.TopMost = false;' 'Splash must not steal focus during authentication.'
Require-Text $nativeProgram 'AeroHelmStartupGate.Authenticate()' 'Startup gate must enforce sign-in before native GCS.'
Require-Text $startupGate 'password.Focus();' 'Password field must receive focus when the modal dialog becomes visible.'
Require-Text $startupGate 'url.Host.EndsWith(".ts.net"' 'Startup sign-in must only use private HTTPS.'
Require-Text $launcher 'AEROHELM_REDUCED_TOOLBAR' 'Operator profile must preserve the established reduced toolbar.'
Require-Text $launcher 'AEROHELM_INTERNAL_TCP_BRIDGE' 'Native single-login adapter opt-in is missing.'
Require-Text $session 'AEROHELM_CAMERA1_URL' 'Camera URL should be configured once from the session.'
Require-Text $session 'UseLegacyPythonBridge' 'Legacy external bridge fallback is missing.'
Require-Text $cloud 'ActiveNativeBridge.Start()' 'Native receive-only bridge startup missing.'
Require-Text $nativeBridge 'IPAddress.Loopback, 5760' 'Native TCP listener must remain loopback only.'
Require-Text $nativeBridge 'DrainGcsAsync' 'GCS-originating bytes must be discarded.'
Require-Text $nativeBridge 'ValidFrame' 'Inbound MAVLink frame envelope must be validated.'
Require-Text $nativeBridge 'TryReadAttitude' 'Real FC roll/pitch decode must be tested.'
Require-Text $nativeBridge 'HasValidTelemetryChecksum' 'HUD HB/ATT must have an integrity gate.'
Require-Text $nativeBridge 'cloudStreamOpen && metrics.HeartbeatFresh' 'Cloud HUD freshness requires a live WSS.'
Require-Text $nativeBridge 'quality.Reset()' 'WSS reconnect must clear previous session health.'
Require-Text $cloudQuality 'Stopwatch.GetTimestamp()' 'Link freshness must use monotonic timestamps.'
Require-Text $project 'AeroHelmLinkQuality.cs' 'Production plugin must compile link diagnostics.'
Require-Text $bridgeHarnessProject 'AeroHelmLinkQuality.cs' 'Offline native tests must compile the same link diagnostics.'
Require-Text $nativeBridge 'HB ' 'Heartbeat freshness must be explicit.'
Require-Text $bridgeContracts 'AeroHelmReceiveOnlyBridge.TryReadAttitude' 'Native parser regression tests missing.'
Require-Text $integratedBuild 'AeroHelmMavlinkBridgeTests.csproj' 'Native bridge tests must run before plugin installation.'
Require-Text $integratedBuild "brandingParams['LogoPath']=" 'Optional custom logo must be passed only when supplied.'
Require-Text $integratedBuild "brandingParams['IconPath']=" 'Optional custom icon must be passed only when supplied.'
Require-Text $integratedBuild "Install-AeroHelmBranding.ps1') @brandingParams" 'Branding must use direct PowerShell splatting, not empty native argument forwarding.'
if ($integratedBuild.Contains('-LogoPath $LogoPath -IconPath $IconPath')) {
    throw 'Branding still forwards empty optional paths to powershell.exe.'
}
Require-Text (Read-Source 'scripts/Install-AeroHelmBranding.ps1') 'build-logs\branding-backups' 'Branding backups must not accumulate in active runtime.'
Require-Text $integratedBuild 'feat/gcs-research-cybershield-twin-ui' 'Build launcher must accept the reviewed research branch.'
Require-Text $integratedBuild 'feat/vihanga-phase3-native-toolbar' 'Build launcher must accept the current Vihanga release branch.'
Require-Text $latestLauncher "feat/vihanga-phase3-native-toolbar" 'Updater must target the current Vihanga release branch.'
Require-Text $consolidation "feat/vihanga-phase3-native-toolbar" 'Consolidated hash manifest must use latest UI source branch.'
Require-Text $generatedCleanup 'Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop' 'Generated-output scan must keep -ErrorAction on the same PowerShell command.'
Require-Text $consolidation "ValidateSet('Inspect','Build','Verify','Diagnose')" 'Release inspection must offer an exact-hash diagnostic mode without writing.'
Require-Text $consolidation 'Git source commit' 'Verification must identify a stale source commit separately from binary mismatches.'
Require-Text $consolidation 'Cloud plugin DLL SHA256' 'Verification must identify a replaced or stale plugin separately.'
Require-Text $generatedCleanup 'if(-not $Apply)' 'Cleanup must default to DRY RUN.'
Require-Text $generatedCleanup 'git -C $root ls-files --cached' 'Cleanup must protect tracked source files.'
Require-Text $generatedCleanup 'Get-Process MissionPlanner' 'Cleanup must not run while Mission Planner is open.'
Require-Text $generatedCleanup 'Consolidate-AeroHelmWindows.ps1' 'Cleanup requires verified runtime.'
Require-Text $generatedCleanup 'Remove-Item -LiteralPath $item.Path' 'Cleanup may delete only enumerated generated paths.'
Require-Text $launcher 'feat/vihanga-phase3-native-toolbar' 'Launcher must pin the reviewed branch.'
Require-Text $launcher 'AEROHELM_AUTO_NATIVE_TCP' 'Cloud receiver setting missing.'
Require-Text $launcher 'Get-NetTCPConnection -LocalPort 5760 -State Listen' 'Local port conflict gate missing.'
Require-Text $nativeBridge 'internal string CompactSummary' 'Native bridge diagnostic missing.'
Require-Text $cloud '_paths.ToolTipText' 'Toolbar needs bounded PATHS diagnostic tooltips.'
Require-Text $mainNative 'Host = "127.0.0.1", Port = "5760"' 'Native Mission Planner TCP should bind local bridge without prompting.'
Require-Text $mainNative 'getparams = false;' 'Any read-only cloud TCP caller must skip parameter requests.'
Require-Text $shared 'AeroHelmReleaseIdentity.BuildId' 'Compiled identity missing.'
Require-Text $shared 'AeroHelmReleaseIdentity.PluginSha256Prefix' 'DLL fingerprint missing.'
Require-Text $consolidation 'Copy-Runtime $runtime $snapshot $false' 'Release migration must backup the entire old runtime BEFORE build.'
Require-Text $consolidation 'Copy-Runtime $snapshot $runtime $true' 'Release migration must restore prior runtime on failure.'
Require-Text $consolidation 'plugin_sha256' 'Release migration must record compiled plugin fingerprint.'
Require-Text $consolidation 'if(-not $Apply)' 'Release must require explicit mutation approval.'
Require-Text $moduleAudit 'SOURCE WIRED' 'Native workspace audit missing.'
Require-Text $legacyNativeLauncher 'throw ''-CachedParameters is not supported' 'Legacy cloud parameter sync bypass must fail closed.'
Require-Text $mainNative 'if (aeroHelmReadOnlyTcp)' 'TCP connection itself must enforce read-only routing policy.'
Require-Text $mainNative 'string.Equals(portname, "TCP", StringComparison.OrdinalIgnoreCase)' 'Read-only policy must not alter native serial/radio parameter downloads.'
Require-Text $cloud 'AeroHelmLinkNavigation.Open(MainV2.instance,' 'Manual link path routing missing.'
Require-Text $paths 'main.doConnect(MainV2.comPort,"TCP","57600"' 'Cloud PATHS must reach native verified MAVLink receive path.'
Require-Text $paths 'PilotDirect' 'PILOT radio backup must remain independently selectable.'
Require-Text $paths 'bridge.Start()' 'A failed initial local cloud bridge must be retryable.'
Require-Text $paths 'NO AUTO-FAILOVER' 'Flight authority must never switch automatically.'
Require-Text $paths 'api/v1/mavlink/paths' 'PATHS must show actual authenticated cloud link evidence.'
Require-Text $cloud 'NewToolbarButton("PATHS"' 'The independent connection module must be visible in toolbar.'
Require-Text $project 'AeroHelmLinkSelector.cs' 'PATHS source must be compiled into plugin.'
Require-Text $project 'AeroHelmCloudAttitudeWindow.cs' 'Real-only cloud attitude viewer must compile into plugin.'
Require-Text $observedAttitude 'api/v1/mavlink/attitude' 'Cloud attitude must be read from JWT-protected real packet endpoint.'
Require-Text $observedAttitude 'canvas.Stale()' 'Cloud attitude must fail closed when stale or unreachable.'
Require-Text $paths 'AeroHelmLinkPath.CloudAttitudePreview' 'PATHS must expose real-only cloud attitude view.'
Require-Text $directBackup 'AcknowledgePilotAuthority' 'Offline native radio backup requires explicit pilot opt-in.'
Require-Text $directBackup '-NoInternalBridge' 'Pilot backup must disable cloud native bridge.'
Require-Text $directBackup '-NoAutoTcp' 'Pilot backup must not auto-bind the cloud transport.'
Require-Text $launcher 'LocalDirectBackup' 'Native direct backup must bypass cloud sign-in only when explicitly selected.'
Require-Text $session '& powershell @launchArgs' 'Session launcher must pass the actual configured argument array.'
Require-Text (Read-Source 'scripts/Install-AeroHelmBranding.ps1') '"Vihanga GCS"' 'Installed product branding must match login gate.'
Require-Text (Read-Source 'scripts/Install-AeroHelmBranding.ps1') "'AeroHelm GCS.xml'" 'Brand migration must consider the old operator settings file.'
Require-Text (Read-Source 'scripts/Install-AeroHelmBranding.ps1') "'Vihanga GCS.xml'" 'Brand migration must preserve the new operator settings file.'
Require-Text (Read-Source 'scripts/Install-AeroHelmBranding.ps1') 'Test-Path -LiteralPath $newProfile -PathType Leaf' 'Brand migration must not overwrite configured Vihanga settings.'
Require-Text (Read-Source 'scripts/Repair-AeroHelmWindows.ps1') "Invoke-FreshReleaseStep -StepArgs @('Build','-Apply','-WithBranding')" 'Normal repair must rebuild current source with branding in a new process.'
Require-Text (Read-Source 'scripts/Repair-AeroHelmWindows.ps1') "Invoke-FreshReleaseStep -StepArgs @('Verify')" 'Normal repair must verify rebuilt binary hashes.'
Require-Text $consolidation '& powershell.exe @buildArgs' 'Release build must use an independent child process with checked exit status.'
Require-Text $cloud 'AeroHelmIconPainter.Make(label,20)' 'AeroHelm toolbar needs per-module vector icons.'
Require-Text $cloud 'main.MenuSimulation.Visible = false' 'Hide Simulation toolbar in default GCS profile.'
Require-Text $cloud 'main.MenuHelp.Visible = false' 'Hide Help toolbar in default GCS profile.'
Require-Text $nativeBridge 'ws.ReceiveAsync' 'Cloud receiver must be authenticated and read only.'
if ($nativeBridge.Contains('ws.SendAsync') -or $nativeBridge.Contains('ws.Send(')) {
    throw 'Native GCS must not send packets over the cloud WebSocket.'
}
Require-Text $launcher 'AEROHELM_CLOUD_READONLY' 'Windows launcher must set read-only cloud environment.'
Require-Text $launcher 'aerohelm-consolidated-release.json' 'Latest GCS launch must check the local native build manifest.'
Require-Text $launcher 'release.executable_sha256' 'Latest GCS launch must reject stale Mission Planner EXE.'
Require-Text $launcher 'release.plugin_sha256' 'Latest GCS launch must reject stale AeroHelm DLL.'
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
$certification = Read-Source 'scripts/Test-VihangaCertification.ps1'
Require-Text $certification 'operational_flight_certified=$false' 'Assessment must never automatically certify flight.'
Require-Text $certification 'exact_native_binary_hashes_verified' 'Assessment must verify installed EXE and plugin hashes.'
Require-Text $certification 'exact_commit_github_ci_passed' 'Assessment must reject absent exact-head CI.'
Require-Text $certification 'private_cloud_pi_preflight_passed' 'Assessment must verify private cloud evidence.'
Require-Text $certification 'external_operator_evidence_present' 'Hardware acceptance must remain independent.'
Write-Host 'PASS: Windows native toolbar and read-only workspace source contracts.' -ForegroundColor Green
Write-Host 'Remaining gates: MSBuild, plugin load, Windows UI smoke, Pi/Pixhawk bench acceptance.'
