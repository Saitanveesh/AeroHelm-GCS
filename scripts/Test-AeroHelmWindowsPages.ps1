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
Require-Text $cloud 'AEROHELM_TOUCH_STARTUP' 'Windows touch startup opt-in is missing.'

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
