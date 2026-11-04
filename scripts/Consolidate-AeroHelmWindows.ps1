# AeroHelm GCS Windows release consolidation and duplicate-plugin cleanup.
# Default is INSPECT ONLY. -Stage Build -Apply is explicit and fully backed up.
# Requires an already fetched/checked-out reviewed GCS development branch.
param(
    [ValidateSet('Inspect','Build','Verify')]
    [string]$Stage='Inspect',
    [switch]$Apply,
    [switch]$WithBranding
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runtime=Join-Path $root 'bin\Release\net461'
$exe=Join-Path $runtime 'MissionPlanner.exe'
$plugins=Join-Path $runtime 'plugins'
$plugin=Join-Path $plugins 'AeroHelm.Cloud.Plugin.Validation.dll'
$logRoot=Join-Path $root 'build-logs'
$manifestPath=Join-Path $logRoot 'aerohelm-consolidated-release.json'
$expectedBranch='feat/gcs-research-cybershield-twin-ui'
$modules=@('FLIGHT','PLAN','VISION','OPS','SECURITY','FLEET','LINK','ANALYTICS','SETTINGS')
$toolbar=@('CLOUD','SURVEY','VISION','LINK','OPS','INTEL','TOUCH')

function Hash([string]$path) {
    if(-not (Test-Path -LiteralPath $path -PathType Leaf)){return $null}
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Assert-Git {
    $branch=(& git -C $root branch --show-current).Trim()
    if($LASTEXITCODE -ne 0 -or $branch -ne $expectedBranch){
        throw "This release requires $expectedBranch (current: $branch). Fetch/switch safely first."
    }
    $dirty=@(& git -C $root status --porcelain --untracked-files=no)
    if($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0){
        throw 'Tracked files have local changes. Review/commit/stash them before release; no reset/clean will be run.'
    }
    $commit=(& git -C $root rev-parse HEAD).Trim()
    if($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-fA-F]{40}$'){
        throw 'Cannot identify the build source commit.'
    }
    return $commit
}
function Copy-Runtime([string]$source,[string]$destination,[bool]$mirror){
    if(-not (Test-Path -LiteralPath $source -PathType Container)){
        throw "Runtime source missing: $source"
    }
    New-Item -Path $destination -ItemType Directory -Force | Out-Null
    # Always operate inside exact runtime/backup paths, never repository root.
    $flags=@('/E','/XJ','/COPY:DAT','/DCOPY:DAT','/R:1','/W:1','/NFL','/NDL','/NJH','/NJS')
    if($mirror){$flags=@('/MIR','/XJ','/COPY:DAT','/DCOPY:DAT','/R:1','/W:1','/NFL','/NDL','/NJH','/NJS')}
    & robocopy.exe $source $destination @flags | Out-Null
    if($LASTEXITCODE -gt 7){throw "Runtime copy failed (robocopy=$LASTEXITCODE)."}
}
function Assert-PluginLayout {
    if(-not (Test-Path -LiteralPath $exe -PathType Leaf)){
        throw "Native Mission Planner executable missing: $exe"
    }
    if(-not (Test-Path -LiteralPath $plugin -PathType Leaf)){
        throw "Compiled AeroHelm plugin missing: $plugin"
    }
    if((Get-Item -LiteralPath $plugin).Length -lt 1024){
        throw 'AeroHelm plugin DLL is implausibly small.'
    }
    if(Test-Path -LiteralPath (Join-Path $plugins 'AeroHelmCloud.cs') -PathType Leaf){
        throw 'Duplicate interpreted AeroHelmCloud.cs is still present in runtime.'
    }
    $conflicts=@(Get-ChildItem -LiteralPath $plugins -File -ErrorAction Stop |
        Where-Object {$_.Name -in @('AeroHelmCloud.dll','AeroHelm.Cloud.Plugin.dll')})
    if($conflicts.Count -gt 0){
        throw "Conflicting legacy DLL remains: $($conflicts.Name -join ', ')"
    }
}
$commit=Assert-Git
# Every release path runs the same nine-panel and toolbar static audit.
& (Join-Path $PSScriptRoot 'Audit-AeroHelmModules.ps1')
Write-Host "AeroHelm source: $expectedBranch @ $($commit.Substring(0,12))"
Write-Host "Runtime: $runtime"
Write-Host "Native EXE SHA-256: $(Hash $exe)"
Write-Host "Compiled plugin SHA-256: $(Hash $plugin)"
Write-Host "Nine panels: $($modules -join ', ')"
Write-Host "Seven AeroHelm toolbar modules: $($toolbar -join ', ')"
if(Test-Path -LiteralPath $plugins -PathType Container){
    $items=@(Get-ChildItem -LiteralPath $plugins -File |
        Where-Object {$_.Name -like 'AeroHelm*'} |
        Select-Object Name,Length)
    Write-Host 'Runtime AeroHelm plugin files:'
    $items | Format-Table -AutoSize | Out-Host
}
# Report a conflicting external Python bridge without shutting it down.
$listener=@(Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue)
if($listener.Count -gt 0){
    Write-Warning "127.0.0.1:5760 is already occupied (PID $($listener[0].OwningProcess)). Close the legacy Python bridge before launching the new internal GCS bridge."
}
if($Stage -eq 'Inspect'){
    Write-Host 'INSPECT ONLY: no compilation, file deletion, git fetch, merge or deployment occurred.'
    Write-Host 'Next: close Mission Planner, update to the reviewed head via git pull --ff-only, then run -Stage Build -Apply.'
    return
}
if($Stage -eq 'Verify'){
    if(-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)){
        throw 'No release manifest found; run -Stage Build -Apply first.'
    }
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-PluginLayout
    if($manifest.git_commit -ne $commit -or
       $manifest.executable_sha256 -ne (Hash $exe) -or
       $manifest.plugin_sha256 -ne (Hash $plugin)){
        throw 'Build manifest does not match CURRENT checkout and runtime. Old or replaced artifacts detected.'
    }
    Write-Host 'VERIFY PASSED: actual EXE/DLL file hashes match the locally recorded release manifest.'
    Write-Host 'Source & artifact verification is NOT a Windows UI smoke test or real aircraft acceptance.'
    return
}
if(-not $Apply){
    throw 'Build changes runtime binaries. Re-run with -Stage Build -Apply after INSPECT and source update.'
}
if(Get-Process MissionPlanner -ErrorAction SilentlyContinue){
    throw 'Close every Mission Planner window BEFORE backup/build. Current binaries will not be overwritten while running.'
}
if(-not (Test-Path -LiteralPath $exe -PathType Leaf)){
    throw 'No existing Mission Planner runtime. Run the reviewed build script manually first.'
}
# Full-runtime backup can be large: refuse to start if the drive cannot
# hold at least one verified copy plus a safety buffer.
$runtimeBytes=(Get-ChildItem -LiteralPath $runtime -Recurse -File -ErrorAction Stop |
    Measure-Object -Property Length -Sum).Sum
$driveLetter=[System.IO.Path]::GetPathRoot($runtime).TrimEnd('\').TrimEnd(':')
$drive=Get-PSDrive -Name $driveLetter -ErrorAction Stop
if($drive.Free -lt ([double]$runtimeBytes * 1.15 + 250MB)){
    throw 'Not enough free disk space to snapshot the complete old GCS runtime safely.'
}
New-Item -Path $logRoot -ItemType Directory -Force | Out-Null
$stamp=(Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$backup=Join-Path $logRoot ('release-backups\' + $stamp)
$snapshot=Join-Path $backup 'runtime'
$oldHash=Hash $exe
Write-Host "Backing up the COMPLETE old runtime to $snapshot"
Copy-Runtime $runtime $snapshot $false
if((Hash (Join-Path $snapshot 'MissionPlanner.exe')) -ne $oldHash){
    throw 'Release backup failed hash verification; original runtime remains untouched.'
}
# Any failure after backup restores the whole former runtime (not just plugin).
try{
    $build=Join-Path $PSScriptRoot 'Build-Integrated-AeroHelm.ps1'
    $global:LASTEXITCODE=0
    if($WithBranding){
        & $build
    } else {
        & $build -SkipBranding
    }
    if($LASTEXITCODE -ne 0){
        throw "Integrated Windows build returned exit code $LASTEXITCODE."
    }
    # Archive only exact known obsolete DLL names; never delete arbitrary
    # Mission Planner or third-party plugins. The installer already archives
    # the dynamic AeroHelmCloud.cs source when it installs the new plugin.
    $knownObsolete=@('AeroHelmCloud.dll','AeroHelm.Cloud.Plugin.dll')
    $old=Join-Path $backup 'obsolete-plugin-archive'
    foreach($name in $knownObsolete){
        $candidate=Join-Path $plugins $name
        if(Test-Path -LiteralPath $candidate -PathType Leaf){
            New-Item -Path $old -ItemType Directory -Force | Out-Null
            Move-Item -LiteralPath $candidate -Destination (Join-Path $old $name) -ErrorAction Stop
        }
    }
    Assert-PluginLayout
    $newHash=Hash $plugin
    $newExeHash=Hash $exe
    if(-not $newHash -or -not $newExeHash){
        throw 'Compiled runtime fingerprint missing after build.'
    }
    $manifest=[ordered]@{
        schema='aerohelm.gcs.consolidation.v1'
        recorded_utc=[DateTime]::UtcNow.ToString('o')
        source_repository='Saitanveesh/AeroHelm-GCS'
        source_branch=$expectedBranch
        git_commit=$commit
        executable_path=$exe
        executable_sha256=$newExeHash
        plugin_path=$plugin
        plugin_sha256=$newHash
        previous_executable_sha256=$oldHash
        complete_previous_runtime_backup=$snapshot
        source_contracts_passed=$true
        native_build_reported_success=$true
        plugin_installer_reported_success=$true
        windows_ui_smoke_verified=$false
        actual_pixhawk_hud_verified=$false
        physical_flight_authorized=$false
        panel_names=$modules
        toolbar_modules=$toolbar
    }
    $tempManifest=$manifestPath + '.tmp'
    $manifest | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $tempManifest -Encoding UTF8 -ErrorAction Stop
    Move-Item -LiteralPath $tempManifest -Destination $manifestPath -Force
    Write-Host "CONSOLIDATED BUILD COMPLETE: $manifestPath" -ForegroundColor Green
    Write-Host "Backup retained: $snapshot"
    Write-Host "Compiled plugin fingerprint: $($newHash.Substring(0,16))"
    Write-Host 'Run -Stage Verify before launching the touch console.'
}
catch {
    $failure=$_
    Write-Warning "Consolidation failed; restoring old runtime from $snapshot"
    try {
        Copy-Runtime $snapshot $runtime $true
        if((Hash $exe) -ne $oldHash){
            throw 'Original executable hash does not match saved snapshot.'
        }
        Write-Warning 'Original runtime restored; existing Git source checkout was NOT reset.'
    }
    catch {
        Write-Error "CRITICAL: automatic runtime restore failed. Keep Mission Planner stopped. Snapshot: $snapshot"
    }
    throw $failure
}
