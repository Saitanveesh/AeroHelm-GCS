# Source-only audit: Vihanga native Mission Planner workspaces and plugin.
param([switch]$Export)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cloud=[IO.File]::ReadAllText((Join-Path $root 'plugins/AeroHelmCloud.cs'))
$project=[IO.File]::ReadAllText((Join-Path $root 'Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj'))
$bindings=@(
 @{name='FLIGHT';source='MainV2.cs';token='MenuFlightData';purpose='Native HUD'},
 @{name='PLAN';source='MainV2.cs';token='MenuFlightPlanner';purpose='Native mission planning'},
 @{name='CLOUD';source='plugins/AeroHelmCloud.cs';token='new CloudWindow()';purpose='Authenticated cloud viewer'},
 @{name='PATHS';source='plugins/AeroHelmCloud.cs';token='new AeroHelmLinkSelector()';purpose='Manual cloud/direct choice'},
 @{name='SURVEY';source='plugins/AeroHelmCloud.cs';token='new AeroHelmSurveyWindow()';purpose='Offline survey drafts'},
 @{name='VISION';source='plugins/AeroHelmCloud.cs';token='new AeroHelmVisionWindow()';purpose='Private camera'},
 @{name='LINK';source='plugins/AeroHelmCloud.cs';token='new AeroHelmLinkWindow()';purpose='Telemetry diagnostics'},
 @{name='OPS';source='plugins/AeroHelmCloud.cs';token='new AeroHelmOpsWindow()';purpose='Simulation and supervised bench'},
 @{name='INTEL';source='plugins/AeroHelmCloud.cs';token='new AeroHelmIntelWindow()';purpose='Mission assurance'},
 @{name='FLEET';source='plugins/AeroHelmCloud.cs';token='new AeroHelmReadOnlyPage("FLEET")';purpose='Fleet inspection'},
 @{name='SECURITY';source='plugins/AeroHelmCloud.cs';token='new AeroHelmReadOnlyPage("SECURITY")';purpose='Security inspection'},
 @{name='ANALYTICS';source='plugins/AeroHelmCloud.cs';token='new AeroHelmReadOnlyPage("ANALYTICS")';purpose='Analytics inspection'},
 @{name='PARAMETERS';source='plugins/AeroHelmCloud.cs';token='new AeroHelmIntelWindow("PARAMETERS")';purpose='Read-only parameter cache'},
 @{name='SETTINGS';source='plugins/AeroHelmCloud.cs';token='new VihangaSettingsWindow()';purpose='Local settings'}
)
$records=@()
foreach($entry in $bindings){
 $file=Join-Path $root $entry.source
 $exists=Test-Path -LiteralPath $file -PathType Leaf
 $wired=$exists -and ([IO.File]::ReadAllText($file)).Contains($entry.token)
 $compiled=($entry.source -eq 'MainV2.cs') -or $project.Contains([IO.Path]::GetFileName($entry.source))
 $records += [pscustomobject]@{
   workspace=$entry.name;source_status=$(if($wired -and $compiled){'SOURCE WIRED'}else{'SOURCE INCOMPLETE'})
   native_windows_runtime='NOT TESTED';physical_hardware='NOT TESTED'
   backend_capabilities='REQUIRES LIVE CHECK';purpose=$entry.purpose
 }
}
$items=@('CLOUD','PATHS','SURVEY','VISION','LINK','OPS','INTEL','MORE')
$missing=@()
foreach($item in $items){
 $needle= if($item -eq 'CLOUD'){'new ToolStripButton("CLOUD")'}
    elseif($item -eq 'MORE'){'new ToolStripDropDownButton("MORE")'}
    else {'NewToolbarButton("'+$item+'"'}
 if(-not $cloud.Contains($needle)){$missing+=$item}
}
if(-not (Test-Path -LiteralPath (Join-Path $root 'plugins/VihangaSharedUi.cs')) -or
   -not $project.Contains('VihangaSharedUi.cs') -or
   (Test-Path -LiteralPath (Join-Path $root 'plugins/AeroHelmTouchPanel.cs')) -or
   $cloud.Contains('AEROHELM_TOUCH_STARTUP')){
 throw 'Obsolete TOUCH UI or missing Vihanga shared components.'
}
Write-Host 'Native GCS source bindings (NOT a Windows or aircraft acceptance test):'
$records | Format-Table workspace,source_status,native_windows_runtime -AutoSize | Out-Host
Write-Host "Toolbar: $($items -join ', ') | Missing: $($missing -join ', ')"
if($Export){
 $target=Join-Path $root 'build-logs/aerohelm-module-audit.json'
 New-Item -Path (Split-Path -Parent $target) -ItemType Directory -Force | Out-Null
 [ordered]@{schema='vihanga.module-audit.v1';audited_utc=[DateTime]::UtcNow.ToString('o');
  source_only=$true;toolbar_missing=$missing;workspace_records=$records;physical_flight_authorized=$false} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $target -Encoding UTF8
 Write-Host "Source audit: $target"
}
if(@($records | Where-Object {$_.source_status -ne 'SOURCE WIRED'}).Count -gt 0 -or $missing.Count -gt 0){
 throw 'Missing Vihanga source bindings; refusing release.'
}
