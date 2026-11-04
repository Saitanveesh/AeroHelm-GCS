# Static release audit of the nine GCS workspaces and toolbar integrations.
# No cloud requests, passwords, drone I/O, deletions or configuration writes.
param([switch]$Export)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$touch=Get-Content -LiteralPath (Join-Path $root 'plugins\AeroHelmTouchPanel.cs') -Raw
$toolbar=Get-Content -LiteralPath (Join-Path $root 'plugins\AeroHelmCloud.cs') -Raw
$project=Get-Content -LiteralPath (Join-Path $root 'Plugins\AeroHelmCloud\AeroHelmCloud.Plugin.csproj') -Raw

$bindings=@(
    @{name='FLIGHT'; token='main.MenuFlightData.PerformClick()'; source='MainV2.cs'; purpose='Native Mission Planner read-only flight view'},
    @{name='PLAN'; token='main.MenuFlightPlanner.PerformClick()'; source='MainV2.cs'; purpose='Native Mission Planner mission drafting'},
    @{name='VISION'; token='new AeroHelmVisionWindow()'; source='plugins\AeroHelmWorkspace.cs'; purpose='Private camera viewer'},
    @{name='OPS'; token='new AeroHelmOpsWindow()'; source='plugins\AeroHelmOperations.cs'; purpose='Bench and mission planning UI'},
    @{name='SECURITY'; token='new AeroHelmReadOnlyPage("SECURITY")'; source='plugins\AeroHelmReadOnlyPages.cs'; purpose='Authenticated security/CyberShield views'},
    @{name='FLEET'; token='new AeroHelmReadOnlyPage("FLEET")'; source='plugins\AeroHelmReadOnlyPages.cs'; purpose='Fleet registry and offline task lab'},
    @{name='LINK'; token='new AeroHelmLinkWindow()'; source='plugins\AeroHelmWorkspace.cs'; purpose='Cloud, Pi and MAVLink diagnostics'},
    @{name='ANALYTICS'; token='new AeroHelmReadOnlyPage("ANALYTICS")'; source='plugins\AeroHelmReadOnlyPages.cs'; purpose='History, digital shadow and twin lab'},
    @{name='SETTINGS'; token='new AeroHelmTouchSettingsWindow()'; source='plugins\AeroHelmTouchPanel.cs'; purpose='Local account and privacy settings'}
)
$records=@()
foreach($entry in $bindings){
    $exists=Test-Path -LiteralPath (Join-Path $root $entry.source) -PathType Leaf
    $wired=$touch.Contains($entry.token)
    $builtInPlugin=$entry.source -like 'plugins\*'
    $listed= -not $builtInPlugin -or $project.Contains([IO.Path]::GetFileName($entry.source))
    $status=if($exists -and $wired -and $listed){'SOURCE WIRED'}else{'SOURCE INCOMPLETE'}
    $records+= [pscustomobject]@{
        workspace=$entry.name
        source_status=$status
        native_windows_runtime='NOT TESTED'
        physical_hardware='NOT TESTED'
        backend_capabilities='M1 deployed; M2/V3 requires separate activation/verification'
        purpose=$entry.purpose
    }
}
$toolbarEntries=@('CLOUD','SURVEY','VISION','LINK','OPS','INTEL','TOUCH')
$toolbarMissing=@()
foreach($item in $toolbarEntries){
    if($item -eq 'CLOUD'){
        if(-not $toolbar.Contains('AeroHelmCloudButton')){$toolbarMissing+=$item}
    }elseif(-not $toolbar.Contains('NewToolbarButton("'+$item+'"')){
        $toolbarMissing+=$item
    }
}
Write-Host 'Nine-panel static source audit (NOT runtime certification):'
$records | Format-Table workspace,source_status,native_windows_runtime -AutoSize | Out-Host
Write-Host "Toolbar modules expected: $($toolbarEntries -join ', ')"
Write-Host "Toolbar modules missing from source: $($toolbarMissing -join ', ')"
Write-Host 'Cloud M2/V3 research services are staged and cannot be marked live by source checks.'
Write-Host 'Real native Windows compilation, DLL loading, cloud auth and HUD remain test gates.'
if($Export){
    $target=Join-Path $root 'build-logs\aerohelm-module-audit.json'
    New-Item -Path (Split-Path -Parent $target) -ItemType Directory -Force | Out-Null
    [ordered]@{
        schema='aerohelm.module-audit.v1'
        audited_utc=[DateTime]::UtcNow.ToString('o')
        source_only=$true
        toolbar_missing=$toolbarMissing
        workspace_records=$records
        physical_flight_authorized=$false
    } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $target -Encoding UTF8
    Write-Host "Static audit report: $target"
}
if(@($records | Where-Object {$_.source_status -ne 'SOURCE WIRED'}).Count -gt 0 -or
   $toolbarMissing.Count -gt 0){
    throw 'Missing GCS sources/bindings. Do NOT attempt to install an incomplete plugin.'
}
