[CmdletBinding()]
param(
    [string]$SourceRoot = 'C:\Users\saita\AeroHelm-GCS',
    [string]$RecoveryRoot = 'C:\Users\saita\Vihanga-KnownGood-20261108',
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$branch = 'restore/vihanga-known-good-20261108-1700'
$remoteRef = 'origin/' + $branch

function Run-Git {
    param([string[]]$Args,[string]$WorkingDirectory)
    & git -C $WorkingDirectory @Args
    if ($LASTEXITCODE -ne 0) {
        throw ('git failed: git -C "' + $WorkingDirectory + '" ' + ($Args -join ' '))
    }
}

if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
    throw "Source checkout not found: $SourceRoot"
}

if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close every Mission Planner/Vihanga window before recovery.'
}

Run-Git -WorkingDirectory $SourceRoot -Args @('fetch','origin',$branch)

if (Test-Path -LiteralPath $RecoveryRoot -PathType Container) {
    $inside = (& git -C $RecoveryRoot rev-parse --is-inside-work-tree 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $inside -ne 'true') {
        throw "Recovery directory already exists but is not a Git worktree: $RecoveryRoot"
    }
    $active = (& git -C $RecoveryRoot branch --show-current).Trim()
    if ($active -cne $branch) {
        throw "Recovery directory uses branch '$active', expected '$branch'. Nothing was overwritten."
    }
    Run-Git -WorkingDirectory $RecoveryRoot -Args @('pull','--ff-only','origin',$branch)
}
else {
    & git -C $SourceRoot show-ref --verify --quiet ('refs/heads/' + $branch)
    if ($LASTEXITCODE -eq 0) {
        Run-Git -WorkingDirectory $SourceRoot -Args @('worktree','add',$RecoveryRoot,$branch)
    }
    else {
        Run-Git -WorkingDirectory $SourceRoot -Args @('worktree','add','-b',$branch,$RecoveryRoot,$remoteRef)
    }
}

$active = (& git -C $RecoveryRoot branch --show-current).Trim()
if ($active -cne $branch) {
    throw "Recovery branch mismatch after worktree creation: $active"
}

$tracked = @(& git -C $RecoveryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $tracked.Count -gt 0) {
    throw 'Tracked source changes exist in the recovery worktree. Nothing will be reset automatically.'
}

Run-Git -WorkingDirectory $RecoveryRoot -Args @('submodule','sync','--recursive')
Run-Git -WorkingDirectory $RecoveryRoot -Args @('submodule','update','--init','--depth','1','--','ExtLibs/mono')

$required = @(
    'ExtLibs\mono\mcs\class\System.Windows.Forms\System.Windows.Forms-net_4_x.csproj',
    'ExtLibs\mono\mcs\class\Accessibility\Accessibility-net_4_x.csproj',
    'ExtLibs\mono\mcs\class\System.Runtime.Serialization.Formatters.Soap\System.Runtime.Serialization.Formatters.Soap-net_4_x.csproj'
)
foreach ($relative in $required) {
    $full = Join-Path $RecoveryRoot $relative
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
        throw "Required Mono project missing after submodule initialization: $relative"
    }
}

Write-Host 'Pinned recovery source and Mono dependency verified.' -ForegroundColor Green

$installer = Join-Path $RecoveryRoot 'scripts\Install-VihangaVerified.ps1'
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -Apply
if ($LASTEXITCODE -ne 0) {
    throw 'Pinned Vihanga build/install verification failed. Existing master checkout was not modified.'
}

$env:AEROHELM_CLOUD_URL = 'https://aerohelm-vcn.tailf3ab6a.ts.net'
$env:AEROHELM_VEHICLE_ID = 'AH-HW-001'
Remove-Item Env:VIHANGA_LINK_ORIGIN -ErrorAction SilentlyContinue
Remove-Item Env:VIHANGA_EXPECTED_ORACLE_INSTANCE_OCID -ErrorAction SilentlyContinue
Remove-Item Env:VIHANGA_ENGINEERING_AUTO_CONNECT -ErrorAction SilentlyContinue
Remove-Item Env:VIHANGA_ENGINEERING_LOCAL_LOGS -ErrorAction SilentlyContinue

$listener = @(Get-NetTCPConnection -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue)
if ($listener.Count -gt 0) {
    $listener | Format-Table LocalAddress,LocalPort,OwningProcess -AutoSize
    throw 'TCP 5760 is occupied. No process was stopped automatically.'
}

if (-not $NoLaunch) {
    $launcher = Join-Path $RecoveryRoot 'scripts\Start-VihangaGCS.ps1'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $launcher
    if ($LASTEXITCODE -ne 0) {
        throw 'Pinned Vihanga launcher failed.'
    }
}

Write-Host ('RECOVERY READY: ' + $RecoveryRoot) -ForegroundColor Green
Write-Host ('BRANCH: ' + $branch) -ForegroundColor Green
Write-Host 'master and the engineering worktree were not reset or cleaned.' -ForegroundColor Green
