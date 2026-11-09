# November 8 2026 ~17:00 IST source candidate (not a recovered binary image).
# Separate checkout, no git reset/clean and no Oracle/Pi/FC modification.
[CmdletBinding()]
param(
    [string]$SourceRoot = 'C:\Users\saita\AeroHelm-GCS',
    [string]$RecoveryRoot = 'C:\Users\saita\Vihanga-Nov08-1700-Recovery',
    [Parameter(Mandatory = $true)][string]$OracleInstanceOcid,
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$branch = 'restore/vihanga-nov08-1700-ist-candidate-20261109'
$origin = 'https://aerohelm-vcn.tailf3ab6a.ts.net:8443'

function Check-Git([string]$Step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed (Git exit $LASTEXITCODE). No reset/clean performed."
    }
}

if ($OracleInstanceOcid -notmatch '^ocid1[.]instance[.]oc[0-9]+[.][A-Za-z0-9_.-]{24,}$') {
    throw 'Supply actual Oracle Compute instance OCID.'
}
if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
    throw "Original source root not found: $SourceRoot"
}
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close all Mission Planner windows before a Windows recovery build.'
}

$fetchRef = '+refs/heads/' + $branch + ':refs/remotes/origin/' + $branch
& git -C $SourceRoot fetch origin $fetchRef
Check-Git 'Fetch recovery branch'

if (Test-Path -LiteralPath $RecoveryRoot -PathType Container) {
    $existing = (& git -C $RecoveryRoot rev-parse --show-toplevel)
    Check-Git 'Inspect existing recovery directory'
    if ([IO.Path]::GetFullPath($existing.Trim()).TrimEnd('\') -ine
        [IO.Path]::GetFullPath($RecoveryRoot).TrimEnd('\')) {
        throw 'Existing recovery directory points to a different Git worktree.'
    }
    $current = (& git -C $RecoveryRoot branch --show-current).Trim()
    Check-Git 'Identify existing recovery branch'
    if ($current -cne $branch) {
        throw "Recovery directory contains branch $current; nothing was overwritten."
    }
    $dirty = @(& git -C $RecoveryRoot status --porcelain --untracked-files=no)
    Check-Git 'Check existing recovery changes'
    if ($dirty.Count -gt 0) {
        throw 'Recovery worktree has tracked source modifications. Nothing was reset.'
    }
    & git -C $RecoveryRoot pull --ff-only origin $branch
    Check-Git 'Fast-forward existing recovery'
} else {
    & git -C $SourceRoot show-ref --verify --quiet ('refs/heads/' + $branch)
    if ($LASTEXITCODE -eq 0) {
        & git -C $SourceRoot worktree add $RecoveryRoot $branch
    } else {
        & git -C $SourceRoot worktree add -b $branch $RecoveryRoot ('origin/' + $branch)
    }
    Check-Git 'Create separate recovery worktree'
}
$actualBranch = (& git -C $RecoveryRoot branch --show-current).Trim()
Check-Git 'Verify recovery branch'
if ($actualBranch -cne $branch) { throw 'Recovery branch mismatch.' }

& git -C $RecoveryRoot submodule sync -- ExtLibs/mono
Check-Git 'Synchronize Mono dependency'
& git -C $RecoveryRoot submodule update --init --depth 1 -- ExtLibs/mono
Check-Git 'Initialize pinned Mono dependency'
foreach ($relative in @(
    'ExtLibs\mono\mcs\class\System.Windows.Forms\System.Windows.Forms-net_4_x.csproj',
    'ExtLibs\mono\mcs\class\Accessibility\Accessibility-net_4_x.csproj',
    'ExtLibs\mono\mcs\class\System.Runtime.Serialization.Formatters.Soap\System.Runtime.Serialization.Formatters.Soap-net_4_x.csproj'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $RecoveryRoot $relative) -PathType Leaf)) {
        throw "Missing required Mono project: $relative"
    }
}

$installer = Join-Path $RecoveryRoot 'scripts\Install-VihangaVerified.ps1'
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -Apply
if ($LASTEXITCODE -ne 0) {
    throw 'Release build/plugin/manifest verification failed; original master not modified.'
}
$env:VIHANGA_LINK_ORIGIN = $origin
$env:VIHANGA_EXPECTED_ORACLE_INSTANCE_OCID = $OracleInstanceOcid
$env:AEROHELM_CLOUD_URL = $origin
$env:AEROHELM_VEHICLE_ID = 'AH-HW-001'
Remove-Item Env:VIHANGA_ENGINEERING_AUTO_CONNECT -ErrorAction SilentlyContinue
Remove-Item Env:VIHANGA_ENGINEERING_LOCAL_LOGS -ErrorAction SilentlyContinue

try {
    $h = Invoke-RestMethod -Method GET -Uri ($origin + '/api/v1/mavlink/health') -TimeoutSec 10
    Write-Host ("Pi online={0} | FC HB={1} | FC ATT={2} | HUD-ready={3} | frames={4}" -f $h.device_online,$h.native_hud_heartbeat_ready,$h.attitude_stream_active,$h.native_quick_data_ready,$h.frames)
    if ($h.native_hud_heartbeat_ready -ne $true -or $h.attitude_stream_active -ne $true) {
        Write-Warning 'FC attitude missing upstream. Windows cannot show live pitch/roll until real ATTITUDE ID30 is forwarded.'
    }
} catch {
    Write-Warning ('Private relay GET not available: ' + $_.Exception.Message)
}

if (-not $NoLaunch) {
    $listeners = @(Get-NetTCPConnection -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue)
    if ($listeners.Count -gt 0) {
        $listeners | Format-Table LocalAddress,LocalPort,OwningProcess -AutoSize
        throw 'Loopback TCP 5760 occupied; do not kill an unknown owner.'
    }
    $launcher = Join-Path $RecoveryRoot 'scripts\Start-VihangaGCS.ps1'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $launcher -LinkOrigin $origin -OracleInstanceOcid $OracleInstanceOcid -NoAutoTcp
    if ($LASTEXITCODE -ne 0) { throw 'Recovery GCS launcher failed.' }
}
Write-Host ('Nov 8 source recovery installed: ' + $RecoveryRoot) -ForegroundColor Green
Write-Host 'Select PATHS -> cloud telemetry after launch; verify real HUD data before concluding.' -ForegroundColor Green
