# AeroHelm unified Windows update/build/start. Default: read-only inspection.
# -Apply: fast-forward reviewed branch, backup old runtime, rebuild and verify.
# -Launch: start only after successful verified compile.
# -StopVerifiedLegacyBridge: explicit opt-in to stop ONLY a checked old Python
# receive-only TCP bridge on port 5760; never kill an unknown process.
param(
    [switch]$Apply,
    [switch]$Launch,
    [switch]$StopVerifiedLegacyBridge
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$branch='feat/gcs-touch-ux-readonly-fleet-r1'
$inspect=Join-Path $PSScriptRoot 'Consolidate-AeroHelmWindows.ps1'
$repair=Join-Path $PSScriptRoot 'Repair-AeroHelmWindows.ps1'
$session=Join-Path $PSScriptRoot 'Start-AeroHelm-Session.ps1'

if ($Launch -and -not $Apply) {
    throw '-Launch requires -Apply and a complete verified build.'
}
if ($StopVerifiedLegacyBridge -and -not $Launch) {
    throw '-StopVerifiedLegacyBridge requires -Launch and explicit -Apply.'
}
$current=(& git -C $root branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $current -ne $branch) {
    throw "Expected $branch; current branch is $current. No files changed."
}
$dirty=@(& git -C $root status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -gt 0) {
    throw 'Tracked source has local changes; review before updating. No reset, clean or forced checkout.'
}
if (-not $Apply) {
    & $inspect -Stage Inspect
    Write-Host 'Inspection only. Use -Apply to fetch, fast-forward, compile and verify.'
    return
}
if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close every Mission Planner window before updating the live GCS EXE/DLL.'
}
Write-Host 'Checking exact reviewed AeroHelm GCS integration branch...'
& git -C $root fetch --prune origin $branch
if ($LASTEXITCODE -ne 0) { throw 'Remote Git fetch failed. No build performed.' }
& git -C $root merge --ff-only ('origin/' + $branch)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to fast-forward. No reset/clean/force actions were performed.'
}
$commit=(& git -C $root rev-parse HEAD).Trim()
Write-Host "Latest reviewed branch at $($commit.Substring(0,12))."
# Critical preflight: parse every production PowerShell launcher using the
# real Windows PowerShell AST parser BEFORE expensive MSBuild or file changes.
# Earlier copied/unterminated launcher blocks passed substring checks and
# prevented the already verified EXE from opening.
$contracts = Join-Path $PSScriptRoot 'Test-AeroHelmWindowsPages.ps1'
& powershell -NoProfile -ExecutionPolicy Bypass -File $contracts
if ($LASTEXITCODE -ne 0) {
    throw 'Updated PowerShell launchers failed syntax/source-contract checks. Build not started.'
}
# Execution uses a fresh script process so the just-updated build/test logic
# is loaded from disk, never stale function definitions in this updater.
& powershell -NoProfile -ExecutionPolicy Bypass -File $repair -Apply
if ($LASTEXITCODE -ne 0) {
    throw 'Native GCS build/install/verification failed. Previous runtime backup retained.'
}
# Verify the exact checkout and binary hashes for this source revision.
& powershell -NoProfile -ExecutionPolicy Bypass -File $inspect -Stage Verify
if ($LASTEXITCODE -ne 0) { throw 'Compiled artifact hashes or source commit are stale.' }
if ($Launch) {
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$session)
    if ($StopVerifiedLegacyBridge) { $arguments += '-StopVerifiedLegacyBridge' }
    Write-Host 'Starting verified latest GCS; no vehicle commands issued.'
    & powershell @arguments
    if ($LASTEXITCODE -ne 0) { throw 'GCS startup preflight failed. No unknown port owner stopped.' }
} else {
    Write-Host 'Verified build ready. Launch separately with scripts/Start-AeroHelm-Session.ps1.'
}
