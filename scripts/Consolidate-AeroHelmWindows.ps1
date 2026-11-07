# AeroHelm GCS Windows release consolidation and duplicate-plugin cleanup.
# Default is INSPECT ONLY. -Stage Build -Apply is explicit and fully backed up.
# Requires an already fetched/checked-out reviewed GCS development branch.
param(
    [ValidateSet('Inspect','Build','Verify','Diagnose','Bootstrap')]
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
$expectedBranch='feat/vihanga-phase3-native-toolbar'
$modules=@('FLIGHT','PLAN','CLOUD','PATHS','SURVEY','VISION','LINK','OPS','INTEL','FLEET','SECURITY','ANALYTICS','PARAMETERS','SETTINGS')
$toolbar=@('CLOUD','PATHS','SURVEY','VISION','LINK','OPS','INTEL','MORE')

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
    # The consolidated plugin contains all custom AeroHelm modules.
    # Unknown secondary AeroHelm DLLs may create duplicate UI/hooks.
    # Never silently delete them: stop with a precise file inventory.
    $conflicts=@(Get-ChildItem -LiteralPath $plugins -File -ErrorAction Stop |
        Where-Object { $_.Name -like 'AeroHelm*.dll' -and
            $_.Name -ne 'AeroHelm.Cloud.Plugin.Validation.dll' })
    if($conflicts.Count -gt 0){
        throw "Extra AeroHelm DLL(s) require explicit review, do not auto-delete: $($conflicts.Name -join ', ')"
    }
}
$commit=Assert-Git
# Every release path runs the same native workspace and toolbar static audit.
& (Join-Path $PSScriptRoot 'Audit-AeroHelmModules.ps1')
Write-Host "AeroHelm source: $expectedBranch @ $($commit.Substring(0,12))"
Write-Host "Runtime: $runtime"
Write-Host "Native EXE SHA-256: $(Hash $exe)"
Write-Host "Compiled plugin SHA-256: $(Hash $plugin)"
Write-Host "Operator workspaces: $($modules -join ', ')"
Write-Host "Vihanga toolbar modules: $($toolbar -join ', ')"
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
if($Stage -in @('Verify','Diagnose')){
    if(-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)){
        throw 'Release manifest missing; nothing is verified. For a fresh empty installation use -Stage Bootstrap -Apply -WithBranding; for an existing runtime use -Stage Build -Apply after backing it up.'
    }
    $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    # Diagnose is read-only and reports mismatches; Verify additionally enforces
    # the installed plugin layout. Neither mode rewrites a manifest or DLL.
    if($Stage -eq 'Verify'){ Assert-PluginLayout }
    $actualExeHash=Hash $exe
    $actualPluginHash=Hash $plugin
    $differences=@()
    foreach($entry in @(
        @{Field='Manifest schema'; Recorded=[string]$manifest.schema; Actual='aerohelm.gcs.consolidation.v1'},
        @{Field='Source branch'; Recorded=[string]$manifest.source_branch; Actual=$expectedBranch},
        @{Field='Git source commit'; Recorded=[string]$manifest.git_commit; Actual=$commit},
        @{Field='MissionPlanner.exe SHA256'; Recorded=[string]$manifest.executable_sha256; Actual=[string]$actualExeHash},
        @{Field='Cloud plugin DLL SHA256'; Recorded=[string]$manifest.plugin_sha256; Actual=[string]$actualPluginHash},
        @{Field='Full native build completed'; Recorded=[string]$manifest.native_build_reported_success; Actual='True'},
        @{Field='Plugin install completed'; Recorded=[string]$manifest.plugin_installer_reported_success; Actual='True'}
    )){
        if(-not [string]::Equals($entry.Recorded,$entry.Actual,[StringComparison]::OrdinalIgnoreCase)){
            $differences += [pscustomobject]@{
                Field=$entry.Field; Recorded=$entry.Recorded; Actual=$entry.Actual
            }
        }
    }
    if($differences.Count -gt 0){
        Write-Warning 'Release verification differences (read-only; no files changed):'
        $differences | Format-Table -AutoSize -Wrap | Out-Host
        if($Stage -eq 'Verify'){
            throw 'Build manifest does not match CURRENT checkout and runtime. Rebuild the exact source with the transactional Windows repair; never edit the manifest to bypass a mismatch.'
        }
        Write-Warning 'DIAGNOSE ONLY: runtime remains unverified. No rebuild, cleanup or flight action was performed.'
        return
    }
    if($Stage -eq 'Diagnose'){
        Write-Host 'DIAGNOSE: all recorded hashes and source identity match. Installed plugin layout has NOT been validated; run -Stage Verify.'
        return
    }
    Write-Host 'VERIFY PASSED: source commit, installed EXE/DLL hashes and build/install state match the locally recorded release manifest.'
    Write-Host 'Source & artifact verification is NOT a Windows UI smoke test or real aircraft acceptance.'
    return
}
$freshBootstrap=($Stage -eq 'Bootstrap')
if(-not $Apply){
    throw 'Build changes runtime binaries. Use -Stage Bootstrap -Apply -WithBranding for a clean clone, or -Stage Build -Apply for an existing verified runtime.'
}
if($freshBootstrap){
    if(Test-Path -LiteralPath $exe -PathType Leaf){
        throw 'Existing MissionPlanner.exe found. Bootstrap cannot overwrite an existing runtime. Use -Stage Build -Apply.'
    }
    # Never delete unexpected files from a purportedly new checkout.
    if(Test-Path -LiteralPath $runtime -PathType Container){
        $existing=@(Get-ChildItem -LiteralPath $runtime -Force -ErrorAction Stop)
        if($existing.Count -ne 0){
            throw 'Fresh bootstrap requires an empty runtime directory. Existing build files were not touched.'
        }
    }
}
if(Get-Process MissionPlanner -ErrorAction SilentlyContinue){
    throw 'Close every Mission Planner window BEFORE backup/build. Current binaries will not be overwritten while running.'
}
if(-not $freshBootstrap -and
   -not (Test-Path -LiteralPath $exe -PathType Leaf)){
    throw 'Existing build runtime not found. Use -Stage Bootstrap for a clean clone.'
}
New-Item -Path $logRoot -ItemType Directory -Force | Out-Null
$backup=$null
$snapshot=$null
$oldHash=$null
if(-not $freshBootstrap){
    # Existing installations always use a full external runtime snapshot.
    $runtimeBytes=(Get-ChildItem -LiteralPath $runtime -Recurse -File -ErrorAction Stop |
        Measure-Object -Property Length -Sum).Sum
    $driveLetter=[System.IO.Path]::GetPathRoot($runtime).Substring(0,1)
    $drive=Get-PSDrive -Name $driveLetter -ErrorAction Stop
    if($drive.Free -lt ([double]$runtimeBytes * 1.15 + 250MB)){
        throw 'Not enough free disk space to snapshot the previous runtime.'
    }
    $stamp=(Get-Date -Format 'yyyyMMdd-HHmmss') + '-' +
        [Guid]::NewGuid().ToString('N').Substring(0,8)
    $backupRoot=Join-Path (Split-Path -Parent $root) 'AeroHelm-GCS-release-backups'
    $backup=Join-Path $backupRoot $stamp
    $snapshot=Join-Path $backup 'runtime'
    $oldHash=Hash $exe
    Write-Host "Backing up complete previous runtime: $snapshot"
    Copy-Runtime $runtime $snapshot $false
    if((Hash (Join-Path $snapshot 'MissionPlanner.exe')) -ne $oldHash){
        throw 'Runtime backup verification failed; original runtime remains untouched.'
    }
}
else {
    Write-Host 'FRESH INSTALL: no prior runtime is present or replaced.'
    Write-Host 'Build must pass before a first-release manifest is created.'
}
# Any failure after backup restores the whole former runtime (not just plugin).
try{
    $build=Join-Path $PSScriptRoot 'Build-Integrated-AeroHelm.ps1'
    # A fresh PowerShell process gives the complete build/test/install sequence
    # a trustworthy success/failure exit code. Never rebaseline the manifest
    # based on stale LASTEXITCODE or a pre-existing cached executable.
    $buildArgs=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass',
                 '-File',$build)
    if(-not $WithBranding){$buildArgs+='-SkipBranding'}
    if($freshBootstrap){$buildArgs+='-SkipLegacySettings'}
    & powershell.exe @buildArgs
    $buildCode=$LASTEXITCODE
    if($buildCode -ne 0){
        throw $(if($freshBootstrap){ "Fresh native build failed (exit $buildCode). No previous runtime exists in this checkout; launch remains blocked." } else { "Integrated Windows build failed (exit $buildCode); restoring the backed-up previous runtime." })
    }
    # Archive only exact known obsolete DLL names; never delete arbitrary
    # Mission Planner or third-party plugins. The installer already archives
    # the dynamic AeroHelmCloud.cs source when it installs the new plugin.
    if(-not $freshBootstrap){
        $knownObsolete=@('AeroHelmCloud.dll','AeroHelm.Cloud.Plugin.dll')
        $old=Join-Path $backup 'obsolete-plugin-archive'
        foreach($name in $knownObsolete){
            $candidate=Join-Path $plugins $name
            if(Test-Path -LiteralPath $candidate -PathType Leaf){
                New-Item -Path $old -ItemType Directory -Force | Out-Null
                Move-Item -LiteralPath $candidate -Destination (Join-Path $old $name) -ErrorAction Stop
            }
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
    if($freshBootstrap){
        Write-Host 'Clean-source first install; previous runtime backup: none (not required).'
    } else {
        Write-Host "Backup retained: $snapshot"
    }
    Write-Host "Compiled plugin fingerprint: $($newHash.Substring(0,16))"
    Write-Host 'Run -Stage Verify before launching the native ground station.'
}
catch {
    $failure=$_
    if($freshBootstrap){
        Write-Warning 'Fresh install failed; no previous binaries exist in this checkout.'
        Write-Warning 'Any partial build files remain unverified. Do not launch.'
    }
    else {
        Write-Warning "Consolidation failed; restoring old runtime from $snapshot"
        try {
            Copy-Runtime $snapshot $runtime $true
            if((Hash $exe) -ne $oldHash){
                throw 'Original executable hash does not match saved snapshot.'
            }
            Write-Warning 'Previous runtime restored; Git source checkout was not reset.'
        }
        catch {
            Write-Error "CRITICAL: automatic runtime restore failed. Keep Mission Planner stopped. Snapshot: $snapshot"
        }
    }
    throw $failure
}
