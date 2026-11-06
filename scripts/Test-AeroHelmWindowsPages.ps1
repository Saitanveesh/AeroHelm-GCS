# Vihanga Vedika Windows source checks: native toolbar, release, and safety.
# Offline only: no COM port, aircraft connection, cloud sign-in or process kill.
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$productionScripts=@(
    'scripts/Start-VihangaVedika-GCS.ps1',
    'scripts/Start-AeroHelm-Session.ps1',
    'scripts/Start-AeroHelm-DirectBackup.ps1',
    'scripts/Update-AeroHelmLatest.ps1',
    'scripts/Repair-AeroHelmWindows.ps1',
    'scripts/Consolidate-AeroHelmWindows.ps1',
    'scripts/Cleanup-AeroHelmGenerated.ps1',
    'scripts/Build-Integrated-AeroHelm.ps1',
    'scripts/Audit-AeroHelmModules.ps1'
)
foreach($relative in $productionScripts){
    $path=Join-Path $root $relative
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){
        throw "Required PowerShell launcher not found: $relative"
    }
    $tokens=$null;$parseErrors=$null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $path,[ref]$tokens,[ref]$parseErrors) | Out-Null
    if(@($parseErrors).Count -gt 0){
        $message=(@($parseErrors)|ForEach-Object{$_.Message}) -join '; '
        throw "PowerShell SYNTAX FAILED in $relative : $message"
    }
}
function Read-Source([string]$relative){
    $path=Join-Path $root $relative
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){
        throw "Required source missing: $relative"
    }
    return [IO.File]::ReadAllText($path)
}
function Require-Text([string]$source,[string]$needle,[string]$reason){
    if(-not $source.Contains($needle)){throw $reason}
}
$toolbar=Read-Source 'plugins/AeroHelmCloud.cs'
$presentation=Read-Source 'plugins/VihangaVedikaPresentation.cs'
$virtual=Read-Source 'plugins/VihangaVedikaCloudR2.cs'
$ops=Read-Source 'plugins/AeroHelmOperations.cs'
$intel=Read-Source 'plugins/AeroHelmPlatformV2.cs'
$paths=Read-Source 'plugins/AeroHelmLinkSelector.cs'
$pilot=Read-Source 'plugins/AeroHelmPilotDirectWindow.cs'
$native=Read-Source 'MainV2.cs'
$bridge=Read-Source 'plugins/AeroHelmReceiveOnlyBridge.cs'
$quality=Read-Source 'plugins/AeroHelmLinkQuality.cs'
$workspace=Read-Source 'plugins/AeroHelmWorkspace.cs'
$project=Read-Source 'Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj'
$mainProject=Read-Source 'MissionPlanner.csproj'
$startup=Read-Source 'AeroHelmStartupGate.cs'
$program=Read-Source 'Program.cs'
$splash=Read-Source 'Splash.Designer.cs'
$launcher=Read-Source 'scripts/Start-VihangaVedika-GCS.ps1'
$session=Read-Source 'scripts/Start-AeroHelm-Session.ps1'
$update=Read-Source 'scripts/Update-AeroHelmLatest.ps1'
$build=Read-Source 'scripts/Build-Integrated-AeroHelm.ps1'
$release=Read-Source 'scripts/Consolidate-AeroHelmWindows.ps1'
$cleaner=Read-Source 'scripts/Cleanup-AeroHelmGenerated.ps1'
$moduleAudit=Read-Source 'scripts/Audit-AeroHelmModules.ps1'
$nativeTests=Read-Source 'Tools/AeroHelmMavlinkBridgeTests/Program.cs'

# Product naming changes UI only; legacy executable, settings, JWT, and DLL
# identifiers are retained for compatibility with existing releases.
Require-Text $presentation 'Vihanga Vedika Ground Control Station' 'Ground station product name missing.'
Require-Text $toolbar 'main.Text = VihangaVedikaProduct.FullName' 'Main native window must use the product name.'
Require-Text $project 'VihangaVedikaPresentation.cs' 'Native toolbar visual asset not compiled.'
Require-Text $project 'VihangaVedikaCloudR2.cs' 'Cloud R2 virtual workflow missing from native plugin.'
foreach($obsolete in @('AeroHelmTouchPanel.cs','AeroHelmReadOnlyPages.cs',
                       'AeroHelmTwinLabWindow.cs','AeroHelmFleetLabWindow.cs')){
    if($project.Contains($obsolete)){throw "Obsolete UI/research model compiled: $obsolete"}
}
if($toolbar.Contains('NewToolbarButton("TOUCH"') -or
   $toolbar.Contains('AEROHELM_TOUCH_STARTUP')){
    throw 'Redundant TOUCH workspace is still active.'
}
foreach($entry in @('PATHS','SURVEY','VISION','LINK','FLEET','OPS','INTEL')){
    Require-Text $toolbar ('NewToolbarButton("'+$entry+'"') "Native toolbar entry missing: $entry"
}
Require-Text $toolbar 'new AeroHelmIntelWindow("FLEET")' 'Direct fleet toolbar entry missing.'
Require-Text $toolbar '"AEROHELM_REDUCED_TOOLBAR"), "1"' 'Native simulation must be visible by default.'
Require-Text $moduleAudit 'SOURCE WIRED' 'Native toolbar audit is not configured.'
Require-Text $moduleAudit 'VihangaVedikaPresentation.cs' 'Module audit must check new presentation file.'

# Independent radio control, real heartbeat/attitude and secure localhost bridge.
Require-Text $pilot 'SerialPort.GetPortNames()' 'Pilot direct radio port selection missing.'
Require-Text $pilot 'MessageBoxButtons.YesNo' 'Pilot direct connection requires explicit confirmation.'
Require-Text $pilot 'main.doConnect(MainV2.comPort,port,baud,true,true)' 'Pilot direct link must remain native.'
Require-Text $paths 'MainV2.comPort.BaseStream.IsOpen' 'Active native link must not be replaced.'
Require-Text $paths 'AeroHelm will not change control authority automatically.' 'Automatic link transfer must remain prohibited.'
Require-Text $bridge 'IPAddress.Loopback, 5760' 'Cloud TCP bridge must bind only to loopback.'
Require-Text $bridge 'DrainGcsAsync' 'Outbound GCS bytes must be discarded.'
Require-Text $bridge 'cloudStreamOpen && metrics.HeartbeatFresh' 'HUD heartbeat must be current.'
Require-Text $bridge 'cloudStreamOpen && metrics.AttitudeFresh' 'HUD attitude must be current.'
Require-Text $bridge 'quality.Reset()' 'Stale-link reset is mandatory.'
Require-Text $quality 'Stopwatch.GetTimestamp()' 'Link freshness must use monotonic time.'
if($bridge.Contains('ws.SendAsync(')){throw 'GCS cloud WSS bridge must not send flight commands.'}
Require-Text $native '"AEROHELM_CLOUD_READONLY"' 'Read-only native transport safety gate missing.'
Require-Text $native 'getparams = false;' 'Cloud viewer must suppress live parameter requests.'

# Simulator-only cloud workflow and useful operator reporting.
Require-Text $ops 'new VihangaVirtualOpsWindow()' 'OPS virtual fleet workbench missing.'
Require-Text $ops 'main.MenuSimulation.PerformClick()' 'Native SITL is inaccessible from OPS.'
foreach($contract in @('api/v1/capabilities','api/v1/missions','api/v1/vehicles/',
                        '/approve','/lease/renew','VIRTUAL_SIMULATOR_ONLY',
                        'physical_flight_authorized','APPROVED_SIM')){
    Require-Text $virtual $contract "OPS R2 missing: $contract"
}
foreach($contract in @('api/v1/fleet/status','api/v1/fleet/metrics',
                        'api/v1/missions?limit=50','api/v1/events?limit=50',
                        'api/v1/cloud-compute/jobs','SortedDictionary<string,double>',
                        'if(parsed.ContainsKey(key))')){
    Require-Text $intel $contract "INTEL R2/parameter handling missing: $contract"
}
if($virtual.Contains('command_long_send') -or
   $virtual.Contains('ws.SendAsync') -or
   $intel.Contains('param_set_send')){
    throw 'Cloud workflow or intelligence UI contains prohibited FC command serialization.'
}

# Transactional native release and startup authentication remain intact.
Require-Text $launcher 'AEROHELM_REQUIRE_LOGIN' 'Cloud-connected startup must require login.'
Require-Text $launcher '--local-maintenance' 'Explicit offline maintenance path missing.'
Require-Text $launcher 'release.executable_sha256' 'Native EXE hash verification missing.'
Require-Text $launcher 'release.plugin_sha256' 'Native plugin hash verification missing.'
Require-Text $launcher 'AEROHELM_CLOUD_READONLY' 'Cloud viewer safety environment missing.'
Require-Text $launcher 'Get-NetTCPConnection -LocalPort 5760' 'Competing TCP listener check missing.'
Require-Text $startup 'AeroHelmStartupGate' 'Startup authentication code missing.'
Require-Text $startup 'AEROHELM_KERBEROS_UI_ENABLED' 'Kerberos must remain explicit opt-in.'
Require-Text $program 'Splash.Hide();' 'Login must not be hidden behind splash.'
Require-Text $splash 'this.TopMost = true;' 'Splash behavior changed: verify Windows modal order.'
Require-Text $session 'Start-VihangaVedika-GCS.ps1' 'Session must use the native GCS launcher.'
Require-Text $update 'merge --ff-only' 'Do not replace source with a forced reset.'
Require-Text $update 'feat/vihanga-vedika-toolbar-ops-intel-phase3' 'Updater must pin current branch.'
Require-Text $build 'AeroHelmMavlinkBridgeTests.csproj' 'Native MAVLink parser tests missing.'
Require-Text $build 'feat/vihanga-vedika-toolbar-ops-intel-phase3' 'Build branch not updated.'
Require-Text $release 'Copy-Runtime $runtime $snapshot $false' 'Previous runtime backup required.'
Require-Text $release 'Copy-Runtime $snapshot $runtime $true' 'Rollback must restore old runtime.'
Require-Text $cleaner 'if(-not $Apply)' 'Cleanup must default to inspection only.'
Require-Text $cleaner 'git -C $root ls-files --cached' 'Cleanup must preserve tracked source.'
Require-Text $nativeTests 'AeroHelmReceiveOnlyBridge.TryReadAttitude' 'Native attitude test missing.'
foreach($excluded in @('obj3','Tools')){
    Require-Text $mainProject ('<Compile Remove="'+$excluded+'\**" />') "Exclude $excluded from native compile."
}
Require-Text $mainProject 'Compile Remove="build-logs\**"' 'Archived runtime source must not compile.'

Write-Host 'PASS: Vihanga Vedika native toolbar, virtual OPS, INTEL and safety source checks.' -ForegroundColor Green
Write-Host 'Remaining acceptance: Windows MSBuild, SITL execution, cloud OIDC and physical disarmed bench.' 
