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
    'scripts/Start-VihangaEngineeringDataLink.ps1',
    'scripts/Test-VihangaPassiveBench.ps1',
    'scripts/Test-VihangaCertification.ps1',
    'scripts/Update-AeroHelmLatest.ps1',
    'scripts/Repair-AeroHelmWindows.ps1',
    'scripts/Consolidate-AeroHelmWindows.ps1',
    'scripts/Cleanup-AeroHelmGenerated.ps1',
    'scripts/Build-Integrated-AeroHelm.ps1',
    'scripts/Install-VihangaVerified.ps1',
    'scripts/Test-VihangaSettingsStorage.ps1'
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
$engineeringLauncher = [IO.File]::ReadAllText(
    (Join-Path $root 'scripts/Start-VihangaEngineeringDataLink.ps1'))
if ([regex]::Matches($engineeringLauncher, '(?m)^if \(\$Build\) \{').Count -ne 1 -or
    [regex]::Matches($engineeringLauncher, '(?m)^\[CmdletBinding\(\)\]').Count -ne 1 -or
    [regex]::Matches($engineeringLauncher, '(?m)^\s*Start-Process -FilePath \$exe').Count -ne 1) {
    throw 'Engineering launcher duplicated, truncated, or missing its single build/launch path.'
}
if (-not $engineeringLauncher.Contains("AEROHELM_CLOUD_READONLY = '1'") -or
    -not $engineeringLauncher.Contains("AEROHELM_AUTO_NATIVE_TCP = '0'")) {
    throw 'Engineering viewer must preserve the receive-only cloud and explicit-connection gates.'
}
# A clean engineering worktree requires Git's pinned Mono submodule to
# satisfy all three project entries in MissionPlanner.sln. This guard checks
# source-only behavior; it does not download dependencies in CI.
$nativeBuilder = [IO.File]::ReadAllText(
    (Join-Path $root 'scripts/Build-AeroHelmGCS.ps1'))
if (-not $engineeringLauncher.Contains('git -C $root submodule update --init --depth 1 -- ExtLibs/mono') -or
    -not $engineeringLauncher.Contains('rev-parse ''HEAD:ExtLibs/mono''') -or
    -not $nativeBuilder.Contains('git submodule update --init --depth 1 -- ExtLibs/mono')) {
    throw 'Native engineering build must provision or diagnose the pinned Mono Git submodule.'
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
# Fail before the expensive native Windows build if a custom plugin class
# was removed or renamed while a constructor call still references it.
# Check precisely the sources that the separate plugin project compiles.
$pluginProjectDir = Join-Path $root 'Plugins/AeroHelmCloud'
$pluginUnits = [regex]::Matches($project, '<Compile\s+Include="([^"]+\.cs)"')
if ($pluginUnits.Count -lt 1) { throw 'Plugin compile source list is empty.' }
$pluginDeclarations = [System.Collections.Generic.HashSet[string]]::new()
$pluginSources = @()
foreach ($unit in $pluginUnits) {
    $relative = $unit.Groups[1].Value.Replace('\','/')
    $sourcePath = [IO.Path]::GetFullPath((Join-Path $pluginProjectDir $relative))
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Compiled plugin source is missing: $relative"
    }
    $text = [IO.File]::ReadAllText($sourcePath)
    $pluginSources += [pscustomobject]@{ Path=$relative; Text=$text }
    foreach ($decl in [regex]::Matches(
        $text, '\b(?:class|struct|enum)\s+([A-Za-z_]\w*)\b')) {
        [void]$pluginDeclarations.Add($decl.Groups[1].Value)
    }
}
foreach ($unit in $pluginSources) {
    foreach ($call in [regex]::Matches(
        $unit.Text, '\bnew\s+((?:AeroHelm|Vihanga|Cloud)[A-Za-z_0-9]+)\s*\(')) {
        $typeName = $call.Groups[1].Value
        if (-not $pluginDeclarations.Contains($typeName)) {
            throw "Unresolved custom plugin constructor $typeName in $($unit.Path)."
        }
    }
}
Require-Text $shared 'VihangaLinkLoginWindow.EnsureSignedIn(this)' 'Settings must open the compiled dedicated relay sign-in.'
Require-Text $shared 'new AeroHelmIntelWindow("FLEET")' 'Legacy cloud fleet must use the compiled read-only intelligence form.'
if ([regex]::IsMatch($shared, '\bnew\s+CloudWindow\s*\(')) {
    throw 'Removed CloudWindow constructor is still referenced in Settings.'
}
$mainProject = Read-Source 'MissionPlanner.csproj'
$settingsCore = Read-Source 'ExtLibs/Utilities/Settings.cs'
# Vihanga operator preferences must save to writable per-user storage and
# complete XML serialization before replacing the previous profile.
Require-Text $settingsCore 'Environment.SpecialFolder.LocalApplicationData' 'Vihanga settings must use per-user application storage.'
Require-Text $settingsCore 'VIHANGA_ENGINEERING_LOCAL_LOGS' 'Engineering telemetry must ignore obsolete inherited logdirectory locations.'
Require-Text $settingsCore 'GetVihangaEngineeringLogDirectory()' 'Engineering telemetry logs need isolated writable LocalAppData storage.'
Require-Text $settingsCore 'Path.Combine(local, "Vihanga GCS", "logs")' 'Native log directory must be a dedicated per-user path.'
Require-Text $engineeringEntry "`$env:VIHANGA_ENGINEERING_LOCAL_LOGS = '1'" 'Engineering launcher must activate local telemetry log isolation.'
Require-Text $engineeringEntry "`$env:VIHANGA_ENGINEERING_AUTO_CONNECT = '1'" 'Engineering launcher must enable the once-only authenticated HUD connection.'
Require-Text $settingsCore 'string.Equals(FileName, "Vihanga GCS.xml"' 'Only the Vihanga profile may change storage location.'
Require-Text $settingsCore 'File.Copy(legacy, staging, false);' 'Previous Vihanga profile must be copied, never moved.'
Require-Text $settingsCore 'File.Replace(temp, filename, filename + ".bak");' 'Settings save must atomically replace the old XML with a backup.'
Require-Text $settingsCore 'File.Move(temp, filename);' 'New settings file must only be moved into place after serialization.'
if ($settingsCore.Contains('new XmlTextWriter(filename, Encoding.UTF8)')) {
    throw 'Settings writer still truncates the active profile before serialization succeeds.'
}
$engineeringEntry = Read-Source 'scripts/Start-VihangaEngineeringDataLink.ps1'
Require-Text $engineeringEntry 'Install-AeroHelmBranding.ps1' 'Engineering build must activate the Vihanga branded LocalAppData profile.'
Require-Text $engineeringEntry 'Test-VihangaSettingsStorage.ps1' 'Engineering build and launch must check Vihanga profile writability.'
Require-Text $engineeringEntry "Join-Path (Split-Path -Parent `$exe) 'logo.txt'" 'Engineering viewer must locate the runtime logo.txt.'
Require-Text $engineeringEntry 'Get-Content -LiteralPath $brandFile -TotalCount 1' 'Engineering launch must inspect the title read by native Program.cs.'
Require-Text $engineeringEntry 'Re-run -Build to activate the LocalAppData profile.' 'Unbranded viewers must be blocked before legacy profile writes.'
$programMain = Read-Source 'Program.cs'
Require-Text $programMain 'Settings.FileName = RemoveInvalidChars(name) + ".xml";' 'Native profile name must derive from runtime branding.'
$brandingStorage = Read-Source 'scripts/Install-AeroHelmBranding.ps1'
Require-Text $brandingStorage "GetFolderPath('LocalApplicationData')" 'Branding profile migration must not write into protected Documents.'
Require-Text $brandingStorage '$legacySettingsDir' 'Branding must preserve the legacy profile location for copy-only migration.'
$profilePreflight = Read-Source 'scripts/Test-VihangaSettingsStorage.ps1'
Require-Text $profilePreflight 'LocalApplicationData' 'Windows settings storage must be tested under the current user.'
Require-Text $profilePreflight '[IO.File]::WriteAllBytes($probe, $payload)' 'Preflight must test actual profile write permission.'
Require-Text $profilePreflight 'Remove-Item -LiteralPath $probe' 'Preflight must clean up only its temporary file.'
Require-Text $profilePreflight '[IO.FileMode]::CreateNew' 'Preflight must validate exclusive Mission Planner log creation.'
Require-Text $profilePreflight "@('tlog','rlog')" 'Both tlog and rlog must pass storage checks.'
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

foreach ($item in @('PATHS','CAMERA')) {
    Require-Text $cloud ('NewToolbarButton("'+$item+'"') "Required operator toolbar entry missing: $item"
}
Require-Text $cloud 'new ToolStripDropDownButton("TOOLS")' 'Consolidated utility menu missing.'
foreach ($item in @('Link diagnostics','Attitude monitor','Survey planner','Vihanga settings')) {
    Require-Text $cloud ('DropDownItems.Add("'+$item+'"') "Consolidated tool missing: $item"
}
foreach ($obsolete in @('new ToolStripButton("CLOUD")',
    'NewToolbarButton("SURVEY"','NewToolbarButton("VISION"',
    'NewToolbarButton("LINK"','NewToolbarButton("OPS"',
    'NewToolbarButton("INTEL"','new ToolStripDropDownButton("MORE")',
    'menu.Items.Insert(menu.Items.IndexOf(_paths)+1,_hudEvidence)')) {
    if ($cloud.Contains($obsolete)) { throw "Obsolete operator toolbar item remains: $obsolete" }
}
Require-Text $cloud 'new VihangaSettingsWindow()' 'Settings must remain accessible through tools.'
Require-Text $cloud 'main.MenuConfigTune' 'Custom controls must follow native Configuration entry.'
Require-Text $cloud 'VIHANGA_ADVANCED_TOOLS' 'Experimental tools must be hidden unless maintenance profile is explicit.'
Require-Text $cloud 'Simulated operations (development)' 'Simulation research must remain available in maintenance mode.'
Require-Text $cloud 'Fleet intelligence (development)' 'Fleet lab must remain available in maintenance mode.'
if ($cloud.Contains('internal sealed class CloudWindow : Form')) {
    throw 'Unreachable legacy CLOUD registry must not ship in the operator plugin.'
}
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
Require-Text $cloud 'active.NativeHeartbeatFresh' 'PATHS must expose native heartbeat freshness in private relay mode.'
Require-Text $cloud 'active.NativeAttitudeFresh' 'PATHS must expose native attitude freshness in private relay mode.'
Require-Text $cloud '_paths.ToolTipText' 'Stale telemetry must remain diagnosable without a duplicate toolbar banner.'
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
Require-Text $integratedBuild 'feat/vihanga-phase3-native-toolbar' 'Build launcher must retain the Vihanga development branch.'
Require-Text $integratedBuild 'master' 'Build launcher must accept merged master.'
Require-Text $latestLauncher 'master' 'Updater must target merged master.'
Require-Text $consolidation "feat/vihanga-phase3-native-toolbar" 'Consolidation must retain development branch compatibility.'
Require-Text $consolidation "master" 'Consolidation must allow merged master.'
Require-Text $generatedCleanup 'Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop' 'Generated-output scan must keep -ErrorAction on the same PowerShell command.'
Require-Text $consolidation "ValidateSet('Inspect','Build','Verify','Diagnose','Bootstrap')" 'Release inspection must offer an exact-hash diagnostic mode without writing.'
Require-Text $consolidation 'Git source commit' 'Verification must identify a stale source commit separately from binary mismatches.'
Require-Text $consolidation 'Cloud plugin DLL SHA256' 'Verification must identify a replaced or stale plugin separately.'
Require-Text $generatedCleanup 'if(-not $Apply)' 'Cleanup must default to DRY RUN.'
Require-Text $generatedCleanup 'git -C $root ls-files --cached' 'Cleanup must protect tracked source files.'
Require-Text $generatedCleanup 'Get-Process MissionPlanner' 'Cleanup must not run while Mission Planner is open.'
Require-Text $generatedCleanup 'Consolidate-AeroHelmWindows.ps1' 'Cleanup requires verified runtime.'
Require-Text $generatedCleanup 'Remove-Item -LiteralPath $item.Path' 'Cleanup may delete only enumerated generated paths.'
Require-Text $launcher 'feat/vihanga-phase3-native-toolbar' 'Launcher must retain reviewed development-branch support.'
Require-Text $launcher 'master' 'Launcher must accept reviewed master.'
Require-Text $launcher '$release.source_branch -ne $branch' 'Launcher must bind manifest to exact checkout branch.'
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
Require-Text $cloud 'AeroHelmLinkNavigation.Open(main,' 'Manual link path routing missing.'
Require-Text $paths 'main.doConnect(MainV2.comPort,"TCP","57600"' 'Cloud PATHS must reach native verified MAVLink receive path.'
Require-Text $paths 'PilotDirect' 'PILOT radio backup must remain independently selectable.'
Require-Text $paths 'bridge.Start()' 'A failed initial local cloud bridge must be retryable.'
Require-Text $paths 'No automatic radio switchover.' 'Flight authority must never switch automatically.'
Require-Text $paths 'internal sealed class VihangaLinkChoice : Control' 'PATHS must use theme-independent accessible surfaces.'
Require-Text $paths 'api/v1/mavlink/paths' 'PATHS must show actual authenticated cloud link evidence.'
# WebException is derived from InvalidOperationException on .NET Framework.
# The more specific handler must precede the generic operational handler.
$refreshStart = $paths.IndexOf('private async Task RefreshEvidenceAsync()')
$refreshEnd = if($refreshStart -ge 0) { $paths.IndexOf('private void Select(', $refreshStart) } else { -1 }
if($refreshStart -lt 0 -or $refreshEnd -lt $refreshStart) {
    throw 'PATHS diagnostics exception contract missing.'
}
$refresh = $paths.Substring($refreshStart, $refreshEnd-$refreshStart)
$httpCatch = $refresh.LastIndexOf('catch(WebException ex)')
$operationCatch = $refresh.IndexOf('catch(InvalidOperationException)')
if($httpCatch -lt 0 -or $operationCatch -lt 0 -or $httpCatch -gt $operationCatch) {
    throw 'PATHS diagnostics must catch WebException before InvalidOperationException.'
}
Require-Text $cloud 'NewToolbarButton("PATHS"' 'The independent connection module must be visible in toolbar.'
Require-Text $project 'AeroHelmLinkSelector.cs' 'PATHS source must be compiled into plugin.'
Require-Text $project 'AeroHelmCloudAttitudeWindow.cs' 'Real-only cloud attitude viewer must compile into plugin.'
Require-Text $project 'VihangaLinkSession.cs' 'Dedicated private relay session must compile into native plugin.'
Require-Text $project 'VihangaLinkLoginWindow.cs' 'Dedicated private relay sign-in must compile into native plugin.'
$relaySession = Read-Source 'plugins/VihangaLinkSession.cs'
$relayLogin = Read-Source 'plugins/VihangaLinkLoginWindow.cs'
$privateRelay = Read-Source 'services/vihanga_link/relay.py'
$privatePi = Read-Source 'services/vihanga_link/pi_agent.py'
$missionPreview = Read-Source 'services/vihanga_link/mission_preview.py'
$controlApi = Read-Source 'services/vihanga_link/control_api.py'
$controlPolicy = Read-Source 'services/vihanga_link/control_policy.py'
$piControlAdapter = Read-Source 'services/vihanga_link/pi_control_adapter.py'
$typedDownlink = Read-Source 'services/vihanga_link/typed_command_downlink.py'
$fcReadiness = Read-Source 'services/vihanga_link/fc_readiness.py'
$localInterlock = Read-Source 'services/vihanga_link/local_gpio_interlock.py'
$simWorkflow = Read-Source 'services/vihanga_link/simulation_workflow.py'
$nativePreview = Read-Source 'plugins/VihangaMissionPreviewWindow.cs'
Require-Text $launcher 'OracleInstanceOcid' 'New Oracle compute OCID must be selected explicitly.'
Require-Text $launcher 'VIHANGA_EXPECTED_ORACLE_INSTANCE_OCID' 'Windows must pin the selected Oracle instance.'
Require-Text $relaySession 'VerifyOracleTargetIdentity()' 'Private sign-in must compare the configured Oracle identity.'
Require-Text $relayLogin 'VihangaLinkSession.VerifyOracleTargetIdentity()' 'Credential exchange must fail closed on wrong Oracle instance.'
Require-Text $privateRelay 'verify_instance_binding(cfg.oracle_instance_ocid)' 'Relay must verify local IMDSv2 OCI identity.'
Require-Text $privatePi 'verify_remote_oracle_instance' 'Pi must verify Oracle VM before transmitting its device token.'
if ($launcher.Contains('https://aerohelm-vcn.tailf3ab6a.ts.net') -or
    $session.Contains('https://aerohelm-vcn.tailf3ab6a.ts.net')) {
    throw 'Windows launchers cannot silently use retired Oracle M1 defaults.'
}
Require-Text $relaySession 'MissionPlanner.Program.AeroHelmOperatorToken' 'Legacy Oracle M1 session must remain independent of new relay.'
Require-Text $relaySession 'VIHANGA_LINK_ORIGIN' 'Dedicated relay origin must be explicit.'
Require-Text $relaySession 'relayExpiresUtc' 'Dedicated viewer JWT must have bounded expiry.'
Require-Text $relayLogin 'VihangaLinkSession.Accept(origin' 'Dedicated relay login must not overwrite M1 JWT.'
Require-Text $paths 'VihangaLinkLoginWindow.EnsureSignedIn(main)' 'PATHS must authenticate the dedicated relay before opening a viewer.'
Require-Text $nativeBridge 'VihangaLinkSession.AccessToken' 'Native WSS bridge must accept the dedicated relay identity.'
Require-Text $nativeBridge 'HasValidGlobalPositionFrame' 'Native cloud map must reject invalid GPS coordinates.'
Require-Text $nativeBridge 'expectedId == 33 && frame[1] < 28' 'GLOBAL_POSITION_INT requires full 28-byte position payload.'
Require-Text $nativeBridge 'NativePositionFresh' 'Windows bridge must expose true GPS position freshness.'
Require-Text $cloud 'Open cloud Flight Data (HUD + GPS map)' 'Direct native HUD/map connection missing.'
Require-Text $cloud 'VIHANGA_ENGINEERING_AUTO_CONNECT' 'Cloud viewer must request authenticated native attachment once on startup.'
Require-Text $cloud 'PATHS: NO ATT' 'Toolbar must distinguish missing physical attitude from link readiness.'
Require-Text $cloud 'PATHS: NO GPS' 'Toolbar must distinguish live attitude from missing physical GPS/map frames.'
Require-Text $cloud 'PATHS: OFFLINE' 'Toolbar must label offline viewer clearly.'
Require-Text $paths 'if (!MainV2.comPort.BaseStream.IsOpen)' 'Native TCP failures must not claim successful cloud HUD connection.'
Require-Text $mainNative 'Cloud telemetry recording could not start' 'Cloud viewer must abort when native telemetry recording fails.'
Require-Text $cloud 'new AeroHelmIntelWindow("PARAMETERS")' 'Read-only cloud parameter inventory must open in desktop.'
Require-Text $paths 'main.doConnect(MainV2.comPort,"TCP","57600",false,false)' 'Native Flight Data must attach to loopback TCP.'
Require-Text (Read-Source 'plugins/AeroHelmPlatformV2.cs') 'ViewFullParameters()' 'Full parameter list must be visible in Windows GCS.'
Require-Text (Read-Source 'plugins/AeroHelmPlatformV2.cs') 'ReadParameters(aircraft, true)' 'Full parameter table must come from authenticated relay.'
Require-Text (Read-Source 'plugins/AeroHelmPlatformV2.cs') 'verified_complete' 'Partial FC parameter tables cannot be represented as complete.'
Require-Text $observedAttitude 'VihangaLinkSession.AccessToken' 'Independent attitude monitor must use the dedicated relay identity.'
Require-Text $bridgeHarnessProject 'VihangaLinkSession.cs' 'Offline bridge harness must compile the actual relay session.'
Require-Text $launcher 'LinkOrigin' 'Verified native launcher must provide explicit relay selection.'
Require-Text $privateRelay 'flight_control_enabled": False' 'New private relay must not grant physical flight control authority.'
Require-Text $privateRelay 'install_control_api(app, cfg.vehicle_id, state' 'Role-separated control authorization API must be isolated from telemetry.'
Require-Text $controlApi 'physical_command_dispatch": False' 'Control endpoint must explicitly deny physical actuation.'
Require-Text $controlApi 'VIHANGA_CONTROL_SIMULATOR_ONLY' 'Flight authority defaults disabled.'
Require-Text $controlApi 'async def approve_plan(' 'Independent approver must authorize exact mission revision.'
Require-Text $controlApi 'record[1] != body.plan_sha256' 'Pilot lease must be bound to approver-confirmed mission digest.'
Require-Text $controlApi 'del context.approvals[body.approval_id]' 'Approver consent must be single use.'
Require-Text $controlPolicy 'FC_ACCEPTED_NOT_EXECUTION_VERIFIED' 'FC acceptance is not flight completion.'
Require-Text $controlPolicy 'rc_override_active' 'Pilot RC override must prevent cloud command authorization.'
Require-Text $piControlAdapter 'interlock or (lambda: PiControlSafety())' 'Unwired Pi interlock must fail closed.'
Require-Text $piControlAdapter 'force-disarm magic value' 'Physical adapter must prohibit emergency override constants by default.'
Require-Text $relaySession 'ReadFlightControlStatus' 'Windows control readiness must not require a flight-token.'
if ($privatePi.Contains('PiCommandAdapter(') -or
    $nativeBridge.Contains('ws.SendAsync(')) {
    throw 'Unreviewed physical dispatch or native cloud-to-FC command tunnel detected.'
}
Require-Text $privateRelay 'maxsize=32' 'Cloud viewer queue must be bounded.'
Require-Text $privatePi 'valid_uart_owner_probe' 'Pi agent must refuse a second UART owner.'
Require-Text $privatePi 'is_disarmed_autopilot_heartbeat' 'Local stream negotiation requires an identified disarmed autopilot.'
Require-Text $privatePi 'request_parameter_snapshot_once' 'Read-only parameter inventory must require an explicit disarmed local request.'
Require-Text $relaySession 'RequestDisarmedTelemetry' 'Private relay telemetry request must have a signed-in client.'
Require-Text $workspace 'Request real FC streams' 'LINK must request missing flight-data streams without duplicating HUD.'
Require-Text $workspace 'RequestDisarmedTelemetry(vehicleId)' 'LINK must use authenticated fixed telemetry-request API.'
Require-Text $privateRelay 'allow_disarmed_telemetry_requests' 'Telemetry duplex must be explicitly opted in on relay.'
Require-Text $privateRelay '@app.post("/api/v1/vehicles/{vehicle_id}/telemetry/probe")' 'Fixed telemetry-request endpoint missing.'
Require-Text $privateRelay 'request_stream_probe' 'Relay must enforce per-session quota and fresh FC evidence.'
Require-Text $privateRelay 'native_quick_data_ready' 'Native Quick needs genuine FC attitude and position evidence.'
Require-Text $privatePi 'queue_disarmed_telemetry_request' 'Pi must authorize a fixed intent only on disarmed UART.'
Require-Text $privatePi '_seen_armed' 'Pi must latch armed-observed state against unsafe cloud probes.'
Require-Text $privatePi 'REFUSED_DISARM_REQUIRED' 'Pi must refuse requests without a fresh disarmed heartbeat.'
Require-Text $cloud 'VIHANGA_ADVANCED_TOOLS' 'Redundant attitude canvas must be hidden by default.'
Require-Text $privateRelay 'def parameter_values(self)' 'Complete-only typed readback must exist in private relay.'
Require-Text $privateRelay 'if msg_id == 22:' 'Cloud parameter snapshot must originate from real MAVLink ID22 frames.'
Require-Text $relaySession 'internal static string ReadParameters' 'Separate relay JWT must expose a GET-only snapshot API.'
Require-Text $privatePi 'fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)' 'Pi UART owner must hold an exclusive process lock.'
Require-Text $privateRelay '@app.post("/api/v1/sim/mission/preview")' 'Cloud planner must expose bounded simulation preview.'
Require-Text $missionPreview 'vehicle_command_authorized": False' 'Autonomy preview may not grant hardware command authority.'
Require-Text $missionPreview 'Route escapes the declared geofence volume' 'Preview must enforce a declared spatial boundary.'
Require-Text $missionPreview 'reserve-adjusted endurance' 'Preview must enforce declared endurance reserve.'
Require-Text $simWorkflow "state='APPROVED_SIM'" 'Simulated mission execution requires explicit prior approval.'
Require-Text $simWorkflow 'Author and approver must be different people' 'Simulator approval must be segregated from authorship.'
Require-Text $simWorkflow 'lease_digest' 'Virtual mission steps require an expiring lease token.'
Require-Text $simWorkflow 'EXPIRED_SIM' 'Timed-out simulation leases must fail closed.'
Require-Text $project 'VihangaMissionPreviewWindow.cs' 'Native plugin must compile the cloud mission preview window.'
Require-Text (Read-Source 'plugins/AeroHelmPlatformV2.cs') 'VihangaLinkSession.ReadParameters' 'Existing PARAMETERS tab must inspect dedicated relay snapshot.'
Require-Text (Read-Source 'plugins/AeroHelmPlatformV2.cs') 'VihangaLinkSession.RequestParameterSnapshot(Id())' 'Dedicated relay parameter refresh must use fixed authenticated read-only API.'
Require-Text $cloud 'new VihangaMissionPreviewWindow()' 'TOOLS must open dedicated cloud simulation preview.'
Require-Text $nativePreview 'api/v1/sim/mission/preview' 'Preview UI must target only the simulation API.'
Require-Text $nativePreview 'SIMULATION_PREVIEW_ONLY' 'Preview UI must reject hardware-authorizing responses.'
if ($nativePreview.Contains('MAV_CMD') -or $nativePreview.Contains('ws.SendAsync(')) {
    throw 'Native cloud autonomy preview may not forward MAVLink commands.'
}
# A private WSS receiver is necessary for the fixed link_ping and
# disarmed_stream_probe diagnostics. Reject physical command paths rather than
# blanket-banning ws.recv(), which falsely fails on the reviewed allowlist.
if ($privateRelay.Contains('/ws/mavlink/control/') -or
    $privatePi.Contains('ws.send_bytes(') -or
    $privatePi.Contains('self.port.write(') -or
    $privatePi.Contains('self.port.mav.send(') -or
    $privatePi.Contains('self.port.mav.set_mode_send(') -or
    $privatePi.Contains('self.port.mav.mission_item_int_send(') -or
    $privatePi.Contains('.mav.param_set_send(') -or
    $privatePi.Contains('.mav.command_int_send(')) {
    throw 'Raw MAVLink control path or bypassing flight-control microservices detected.'
}
Require-Text $privatePi 'raw = await ws.recv()' 'Private diagnostic receiver must be explicit.'
Require-Text $privatePi 'if not isinstance(raw, str) or len(raw) > MAX_CONTROL_TEXT:' 'Authenticated Pi receiver must reject binary MAVLink and oversized typed requests.'
Require-Text $privatePi 'MAX_CONTROL_TEXT = 4096' 'Typed control frame size must remain bounded.'
Require-Text $typedDownlink 'PHYSICAL_ACTIONS = frozenset({"ARM", "DISARM"})' 'Only ARM/DISARM typed downlink may reach physical adapter.'
Require-Text $typedDownlink 'self._epoch is None or self._epoch != epoch' 'Pi commands require Oracle-issued session epoch.'
Require-Text $typedDownlink 'self.hardware_gate()' 'Pi UART must recheck physical interlock immediately before command.'
Require-Text $fcReadiness 'class PhysicalFCObserver' 'Physical FC evidence must originate from one sole-UART observer.'
Require-Text $localInterlock 'class LocalGPIOInterlock' 'Root-controlled independent local safety key is required.'
Require-Text $privatePi 'self._typed_control.service_uart(self.port)' 'Only existing UART owner may issue typed commands.'
Require-Text $controlApi 'observe_fc_safety_evidence' 'Oracle must validate Pi FC safety report from device WSS.'
Require-Text $privateRelay '"type": "control_session_epoch"' 'Oracle must bind command session to authenticated Pi.'
Require-Text $nativeBridge 'Task drain = DrainGcsAsync(' 'Native TCP must continue discarding raw outbound MAVLink.'
Require-Text $privatePi 'message = json.loads(raw)' 'Private diagnostic receiver must accept typed JSON only.'
Require-Text $privatePi 'pong = self.transport_ping_reply(message)' 'Only the fixed diagnostic ping may be answered without FC access.'
Require-Text $privatePi 'ack = self.queue_disarmed_telemetry_request(message)' 'Private FC request must use the disarmed telemetry-only whitelist.'
Require-Text $privatePi 'message.get("type") != "link_ping"' 'Private diagnostic ping must require an exact intent type.'
Require-Text $privatePi 'message.get("type") != "disarmed_stream_probe"' 'Cloud may request only the fixed disarmed telemetry probe.'
Require-Text $privatePi 'if not (cfg.stream_probe or cfg.recover_streams) or not is_disarmed_autopilot_heartbeat(' 'Automatic stream recovery still requires disarmed FC and explicit opt-in.'
Require-Text $privatePi 'MAV_CMD_SET_MESSAGE_INTERVAL' 'Physical UART stream reconfiguration must use bounded volatile telemetry intervals.'
Require-Text $privatePi 'queue_disarmed_parameter_request' 'Read-only parameter intent must be explicitly authorized on Pi.'
Require-Text $privatePi 'param_request_list_send' 'Physical parameter acquisition must use read-only PARAM_REQUEST_LIST.'
Require-Text $privateRelay 'allow_disarmed_parameter_requests' 'Relay parameter request must default to disabled.'
Require-Text $privateRelay '@app.post("/api/v1/vehicles/{vehicle_id}/parameters/refresh")' 'Private parameter readback must have a fixed authenticated endpoint.'
Require-Text $relaySession 'internal static string RequestParameterSnapshot' 'Windows parameter capture must use separate relay token.'
if ([regex]::Matches($privatePi, '\.mav\.command_long_send\s*\(').Count -ne 1 -or
    [regex]::Matches($privatePi, 'ws\.recv\s*\(').Count -ne 1) {
    throw 'Private Pi UART or cloud receive command surface expanded; audit command whitelist before shipping.'
}
Require-Text $observedAttitude 'AeroHelmReceiveOnlyBridge.PrivateWebSocketUri()' 'Cloud attitude preview must use authenticated MAVLink WSS rather than an undeployed REST endpoint.'
Require-Text $observedAttitude 'ws.Options.SetRequestHeader("Authorization"' 'Attitude WSS must use the existing operator session.'
Require-Text $observedAttitude 'AeroHelmReceiveOnlyBridge.TryReadAttitude(' 'Attitude preview must decode genuine CRC-verified MAVLink packets.'
Require-Text $observedAttitude 'quality.Snapshot()' 'Attitude preview must enforce packet freshness.'
Require-Text $observedAttitude 'OtherMessageIdSummary()' 'Cloud monitor must expose other MAVLink message IDs to diagnose heartbeat-only streams.'
Require-Text $observedAttitude 'messageIds[id]++' 'Cloud monitor must count real observed message IDs.'
Require-Text $observedAttitude 'MAVn_EXTRA1/SRx_EXTRA1' 'Cloud monitor must expose the per-port ATTITUDE stream-rate diagnostic.'
Require-Text $observedAttitude 'ID 30 missing:' 'Missing attitude must be surfaced without invented roll/pitch.'
if($observedAttitude.Contains('api/v1/mavlink/attitude') -or
   $observedAttitude.Contains('ws.SendAsync(') -or
   $observedAttitude.Contains('ws.Send(')){
    throw 'Cloud attitude preview must neither rely on M1 REST endpoints nor send aircraft commands.'
}
Require-Text $observedAttitude 'canvas.Stale()' 'Cloud attitude must fail closed when stale or unreachable.'
Require-Text $cloud 'ShowWorkspace(new AeroHelmCloudAttitudeWindow())' 'Attitude monitor must remain accessible under TOOLS.'
if ($paths.Contains('new VihangaLinkChoice("Attitude monitor"')) {
    throw 'Attitude monitor is diagnostics, not a third connection path.'
}
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
Require-Text $consolidation 'if($freshBootstrap)' 'Bootstrap must be guarded separately from transactional repairs.'
Require-Text $consolidation '-SkipLegacySettings' 'Clean bootstrap must avoid restoring old Mission Planner settings.'
Require-Text (Read-Source 'GCSViews/FlightData.cs') 'new ToolStripMenuItem("Switch tab")' 'Native Flight tabs must remain accessible in one row.'
Require-Text $cloud 'AeroHelmIconPainter.Make(label,20)' 'AeroHelm toolbar needs per-module vector icons.'
Require-Text $cloud 'main.MenuSimulation.Visible = false' 'Hide Simulation toolbar in default GCS profile.'
Require-Text $cloud 'main.MenuHelp.Visible = false' 'Hide Help toolbar in default GCS profile.'
Require-Text $nativeBridge 'ws.ReceiveAsync' 'Cloud receiver must be authenticated and read only.'
Require-Text $nativeBridge 'internal static Uri PrivateWebSocketUri()' 'MAVLink WSS URI must be shared between the native bridge and the attitude monitor.'
if ($nativeBridge.Contains('ws.SendAsync') -or $nativeBridge.Contains('ws.Send(')) {
    throw 'Native GCS must not send packets over the cloud WebSocket.'
}
Require-Text $launcher 'AEROHELM_CLOUD_READONLY' 'Windows launcher must set read-only cloud environment.'
$verifiedInstall = Read-Source 'scripts/Install-VihangaVerified.ps1'
Require-Text $verifiedInstall "Invoke-CheckedScript 'Windows source contracts'" 'Verified installer must check source before build.'
Require-Text $verifiedInstall "Invoke-CheckedScript 'Release manifest, executable and plugin verification'" 'Verified installer must check artifact hashes before startup.'
Require-Text $verifiedInstall 'if ($code -ne 0)' 'Installer must stop on a failed child process.'
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
