# AeroHelm generated-artifact cleanup. DRY RUN unless -Apply is explicit.
# Requires the verified latest GCS source and runtime; does not touch aircraft.
# This does NOT prune Git history, release backups, cloud secrets, flight logs,
# or Mission Planner's essential bin/Release/net461 runtime.
param([switch]$Apply)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$branch='feat/gcs-touch-ux-readonly-fleet-r1'
$branchNow=(& git -C $root branch --show-current).Trim()
if($LASTEXITCODE -ne 0 -or $branchNow -ne $branch){
    throw "Cleanup requires reviewed branch $branch (found $branchNow)."
}
if(Get-Process MissionPlanner -ErrorAction SilentlyContinue){
    throw 'Close all Mission Planner windows before cleanup.'
}
$dirty=@(& git -C $root status --porcelain --untracked-files=no)
if($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0){
    throw 'Tracked source has local edits. Save or review them; no forced cleanup.'
}
# Never remove the active Mission Planner runtime, DLL release snapshot,
# historical flight logs or operator config. The 9 paths below are rebuilt
# automatically by .NET or Python tests. No wildcard applied at repo root.
$relatives=@(
    'Plugins/AeroHelmCloud/bin',
    'Plugins/AeroHelmCloud/obj',
    'Tools/AeroHelmMavlinkBridgeTests/bin',
    'Tools/AeroHelmMavlinkBridgeTests/obj',
    'Tools/AeroHelmSurveyTests/bin',
    'Tools/AeroHelmSurveyTests/obj',
    'Tools/__pycache__',
    'Tools/.pytest_cache',
    'cloud/.pytest_cache'
)
$candidates=@()
foreach($relative in $relatives){
    $path=Join-Path $root ($relative.Replace('/','\'))
    if(-not (Test-Path -LiteralPath $path)){continue}
    $item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if(-not $item.PSIsContainer -or
       (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)){
        throw "Refusing cleanup of a non-directory or linked target: $relative"
    }
    $full=[IO.Path]::GetFullPath($path)
    if(-not $full.StartsWith($root+[IO.Path]::DirectorySeparatorChar,
                              [StringComparison]::OrdinalIgnoreCase)){
        throw "Cleanup path escapes checkout: $relative"
    }
    # Never delete tracked files even when accidentally placed in build output.
    $tracked=@(& git -C $root ls-files --cached -- $relative)
    if($LASTEXITCODE -ne 0 -or $tracked.Count -gt 0){
        throw "Cleanup target contains tracked files: $relative"
    }
    # Scan one level at a time. Never recurse THROUGH junctions or symlinks
    # even during the dry-run inspection (Windows PowerShell 5.1 safe).
    $pending=New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($full)
    [long]$size=0
    while($pending.Count -gt 0){
        $directory=$pending.Pop()
        foreach($child in @(Get-ChildItem -LiteralPath $directory -Force
                             -ErrorAction Stop)){
            if(($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){
                throw "Cleanup target contains a reparse point; inspect: $($child.FullName)"
            }
            if($child.PSIsContainer){
                $pending.Push($child.FullName)
            } else {
                $size += [long]$child.Length
            }
        }
    }
    $candidates+= [pscustomobject]@{Relative=$relative;Path=$full;Bytes=$size}
}
Write-Host "Generated build/test directories found: $($candidates.Count)"
$candidates | Select-Object Relative,@{N='MiB';E={[math]::Round($_.Bytes/1MB,1)}} |
    Format-Table -AutoSize | Out-Host
if(-not $Apply){
    Write-Host 'DRY RUN ONLY. No directory removed.'
    Write-Host 'To delete ONLY the paths listed above: .\scripts\Cleanup-AeroHelmGenerated.ps1 -Apply'
    Write-Host 'Runtime, last-good backups, logs, models tracked by Git and secrets are preserved.'
    return
}
# Run after a successful backed-up build and its exact-commit hash check.
$verify=Join-Path $PSScriptRoot 'Consolidate-AeroHelmWindows.ps1'
& $verify -Stage Verify
if(-not $?){throw 'Verified GCS release required before deleting generated outputs.'}
foreach($item in $candidates){
    Write-Host "Removing generated-only directory: $($item.Relative)"
    Remove-Item -LiteralPath $item.Path -Recurse -Force -ErrorAction Stop
}
Write-Host 'CLEANUP COMPLETE. No active runtime, source, cloud secrets, backups or logs removed.'
