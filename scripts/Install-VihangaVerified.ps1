# Verified first install or transactional rebuild for the Vihanga Windows GCS.
# This entry point never bypasses release integrity or aircraft-control gates.
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$allowedBranches = @('feat/vihanga-phase3-native-toolbar', 'restore/vihanga-known-good-20261107-1438', 'restore/vihanga-known-good-20261108-1700')

if (-not $Apply) {
    throw 'Installation changes runtime files. Re-run with -Apply after reviewing the current source branch.'
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Vihanga native installation requires Windows.'
}
if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close Mission Planner before building or verifying the installation.'
}
$branch = (& git -C $root branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -notin $allowedBranches) {
    throw "Expected a reviewed Vihanga release branch; currently on '$branch'. Update manually using git pull --ff-only."
}
$trackedChanges = @(& git -C $root status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $trackedChanges.Count -gt 0) {
    throw 'Tracked local source changes detected. Review them before installation; no reset or clean will be performed.'
}

function Invoke-CheckedScript([string]$step,[string]$script,[string[]]$scriptArgs) {
    $full = Join-Path $PSScriptRoot $script
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "$step script missing: $full"
    }
    Write-Host "STEP: $step" -ForegroundColor Cyan
    $arguments = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$full) + $scriptArgs
    & powershell.exe @arguments
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        throw "$step failed (exit code $code). Release not verified; startup blocked."
    }
}

$runtime = Join-Path $root 'bin\Release\net461'
$exe = Join-Path $runtime 'MissionPlanner.exe'
$stage = 'Build'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    $stage = 'Bootstrap'
    if (Test-Path -LiteralPath $runtime -PathType Container) {
        $partial = @(Get-ChildItem -LiteralPath $runtime -Force -ErrorAction Stop)
        if ($partial.Count -gt 0) {
            Write-Warning 'Partial first-build runtime detected. Bootstrap will preserve an external SHA256-verified snapshot before retrying.'
        }
    }
} elseif (-not (Test-Path -LiteralPath (Join-Path $root 'build-logs\aerohelm-consolidated-release.json') -PathType Leaf)) {
    Write-Warning 'Existing native EXE has no release manifest. Build will back up the entire unverified runtime before rebuilding.'
}
Invoke-CheckedScript 'Profile storage preflight' 'Test-VihangaSettingsStorage.ps1' @()
Invoke-CheckedScript 'Windows source contracts' 'Test-AeroHelmWindowsPages.ps1' @()
Invoke-CheckedScript "Native release $stage" 'Consolidate-AeroHelmWindows.ps1' @('-Stage',$stage,'-Apply','-WithBranding')
Invoke-CheckedScript 'Release manifest, executable and plugin verification' 'Consolidate-AeroHelmWindows.ps1' @('-Stage','Verify')
Write-Host 'VIHANGA VERIFIED: source commit and installed binary hashes match the release manifest.' -ForegroundColor Green
Write-Host 'Windows UI, cloud/bridge and disarmed hardware acceptance remain separate checks.'
if ($Launch) {
    Invoke-CheckedScript 'Native GCS startup' 'Start-VihangaGCS.ps1' @('-NoAutoTcp')
}
