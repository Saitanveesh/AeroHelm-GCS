# Source-only Vihanga Vedika native toolbar and safety audit.
# No network calls, UAV connections, source deletion or configuration writes.
param([switch]$Export)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$toolbar=Get-Content -LiteralPath (Join-Path $root 'plugins\AeroHelmCloud.cs') -Raw
$project=Get-Content -LiteralPath (Join-Path $root 'Plugins\AeroHelmCloud\AeroHelmCloud.Plugin.csproj') -Raw

$bindings=@(
    @{name='CLOUD';   source='plugins\AeroHelmCloud.cs'; token='new CloudWindow()'; purpose='Private cloud sign-in and view sessions'},
    @{name='PATHS';   source='plugins\AeroHelmLinkSelector.cs'; token='new AeroHelmLinkSelector()'; purpose='Explicit cloud viewer or pilot radio selection'},
    @{name='SURVEY';  source='plugins\AeroHelmSurveyPlanner.cs'; token='new AeroHelmSurveyWindow()'; purpose='Local survey planning and draft export'},
    @{name='VISION';  source='plugins\AeroHelmWorkspace.cs'; token='new AeroHelmVisionWindow()'; purpose='Private camera viewer'},
    @{name='LINK';    source='plugins\AeroHelmWorkspace.cs'; token='new AeroHelmLinkWindow()'; purpose='Link quality and telemetry diagnostics'},
    @{name='FLEET';   source='plugins\AeroHelmPlatformV2.cs'; token='new AeroHelmIntelWindow("FLEET")'; purpose='Cloud fleet observation'},
    @{name='OPS';     source='plugins\AeroHelmOperations.cs'; token='new AeroHelmOpsWindow()'; purpose='Virtual mission workflow and native SITL'},
    @{name='INTEL';   source='plugins\AeroHelmPlatformV2.cs'; token='new AeroHelmIntelWindow()'; purpose='Parameters, history, audit and cloud reports'}
)
$records=@()
$missing=@()
foreach($entry in $bindings){
    $path=Join-Path $root $entry.source
    $exists=Test-Path -LiteralPath $path -PathType Leaf
    $sourceText=if($exists){Get-Content -LiteralPath $path -Raw}else{''}
    $built=$project.Contains([IO.Path]::GetFileName($entry.source))
    # Some entry points belong in the toolbar plugin but the callable type
    # is implemented in another compiled source file.
    $wired=$toolbar.Contains($entry.token) -and
           ($sourceText.Contains('class ') -or $entry.name -eq 'CLOUD')
    $status=if($exists -and $wired -and $built){'SOURCE WIRED'}else{'SOURCE INCOMPLETE'}
    if($entry.name -eq 'CLOUD'){$wired=$toolbar.Contains('new CloudWindow()')}
    $records+=[pscustomobject]@{
        module=$entry.name; source_status=$status;
        windows_runtime='NOT TESTED'; aircraft='NOT TESTED';
        purpose=$entry.purpose
    }
    if($status -ne 'SOURCE WIRED'){$missing+=$entry.name}
}
foreach($module in @('PATHS','SURVEY','VISION','LINK','FLEET','OPS','INTEL')){
    if(-not $toolbar.Contains('NewToolbarButton("'+$module+'"')){$missing+=$module}
}
foreach($required in @('VihangaVedikaPresentation.cs','VihangaVedikaCloudR2.cs',
                        'AeroHelmOperations.cs','AeroHelmPlatformV2.cs')){
    if(-not $project.Contains($required)){$missing+=$required}
}
if($project.Contains('AeroHelmTouchPanel.cs') -or
   $project.Contains('AeroHelmReadOnlyPages.cs') -or
   $toolbar.Contains('NewToolbarButton("TOUCH"') -or
   $toolbar.Contains('AEROHELM_TOUCH_STARTUP')){
    $missing+='Redundant TOUCH UI remains enabled'
}
if(-not $toolbar.Contains('main.MenuSimulation.Visible = false') -or
   -not $toolbar.Contains('"AEROHELM_REDUCED_TOOLBAR"), "1"')){
    $missing+='Native SITL visibility is not explicit/opt-in'
}
$nativeBridge=Get-Content -LiteralPath (Join-Path $root 'plugins\AeroHelmReceiveOnlyBridge.cs') -Raw
if($nativeBridge.Contains('ws.SendAsync(') -or
   -not $nativeBridge.Contains('DrainGcsAsync')){
    $missing+='Cloud bridge transmit guard'
}
Write-Host 'Vihanga Vedika native toolbar audit (source only):'
$records | Format-Table module,source_status,windows_runtime -AutoSize | Out-Host
if($Export){
    $output=Join-Path $root 'build-logs\vihanga-module-audit.json'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    [ordered]@{
        schema='vihanga.gcs.module-audit.v2'
        checked_utc=[DateTime]::UtcNow.ToString('o')
        source_only=$true
        modules=$records
        failures=$missing
        aircraft_control_authorized=$false
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding UTF8
    Write-Host "Audit report: $output"
}
if($missing.Count -gt 0){
    throw "Incomplete native toolbar or safety contract: $($missing -join ', ')"
}
Write-Host 'PASS: native toolbar sources present. Windows compilation and aircraft acceptance remain independent.'
