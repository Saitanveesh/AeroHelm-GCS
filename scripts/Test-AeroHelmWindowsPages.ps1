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
$workspace = Read-Source 'plugins/AeroHelmWorkspace.cs'
$operations = Read-Source 'plugins/AeroHelmOperations.cs'
$project = Read-Source 'Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj'
$cloud = Read-Source 'plugins/AeroHelmCloud.cs'
$installer = Read-Source 'scripts/Install-AeroHelmCloud.ps1'
$rollback = Read-Source 'scripts/Restore-AeroHelmPlugin.ps1'
$roundtrip = Read-Source 'scripts/Test-AeroHelmPluginRoundtrip.ps1'
$launcher = Read-Source 'scripts/Start-AeroHelm-Touch.ps1'

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
Require-Text $touch 'MainV2.instance' 'Mission Planner must remain the native root.'
Require-Text $touch 'Keys.Escape' 'Touch view must retain Escape-to-close.'
Require-Text $touch 'RefreshCloudStatusAsync' 'HOME authenticated link status is missing.'
Require-Text $touch 'api/v1/mavlink/health' 'HOME status must use read-only MAVLink health.'
Require-Text $touch 'CLOUD / STALE OR DISCONNECTED' 'HOME must report stale aircraft status.'
Require-Text $touch 'CLOUD / UNAVAILABLE' 'HOME needs a fail-closed disconnected state.'
Require-Text $cloud 'AEROHELM_TOUCH_STARTUP' 'Windows touch startup opt-in is missing.'
Require-Text $cloud 'AEROHELM_REDUCED_TOOLBAR' 'Native HELP and Simulation menus must be retained by default.'
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
Require-Text $workspace 'AeroHelmWorkspaceSettings.CheckedPrivateUrl(url.Text)' 'Camera URL validation missing.'
Require-Text $workspace 'refreshTimer.Interval = 15000' 'LINK bounded polling setting missing.'
Require-Text $workspace 'refreshTimer.Stop()' 'LINK watcher must stop on close.'
Require-Text $operations 'CAPABILITIES — READ ONLY' 'OPS policy page is missing.'
Require-Text $operations 'Api("GET","api/v1/operations/capabilities")' 'OPS policy must be a read-only GET.'
Require-Text $project 'AeroHelmReadOnlyPages.cs' 'Native plugin project must compile new pages.'
Write-Host 'PASS: Windows nine-tile wiring and read-only page contract checks.' -ForegroundColor Green
Write-Host 'Remaining gates: MSBuild, native plugin load, Windows touch UX, Pi/Pixhawk bench tests.'
