# AeroHelm Windows integration gate. No network requests, secrets or Pixhawk
# commands. Source is default; Build is an explicit local opt-in and can
# replace build outputs. Neither stage grants flight authority.
param(
    [ValidateSet('Source','Build','Artifacts')]
    [string]$Stage = 'Source',
    [switch]$SkipBranding
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $root 'bin\Release\net461\MissionPlanner.exe'
$plugin = Join-Path $root 'bin\Release\net461\plugins\AeroHelm.Cloud.Plugin.Validation.dll'
$logDir = Join-Path $root 'build-logs'

function Artifact([string]$path,[string]$name) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [ordered]@{name=$name;present=$false;sha256=$null;size_bytes=$null}
    }
    $file = Get-Item -LiteralPath $path
    return [ordered]@{
        name=$name;present=$true
        sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        size_bytes=$file.Length
    }
}
$actualBranch = (& git -C $root branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($actualBranch)) {
    throw 'Detached/unknown Git branch. Select the reviewed development branch.'
}
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to establish reproducible Git commit.'
}
if ($actualBranch -notin @('feat/aerohelm-touch-console',
                           'feat/aerohelm-windows-readiness-v2',
                           'feat/aerohelm-windows-release-candidate')) {
    throw "Unreviewed Windows release branch: $actualBranch"
}
$dirty = @(& git -C $root status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0) { throw 'Cannot verify local Git worktree.' }
if ($dirty.Count -gt 0) {
    throw 'Tracked source modifications are present. Commit/stash before recording a reproducible readiness result.'
}
if ($Stage -eq 'Build' -and (Get-Process MissionPlanner -ErrorAction SilentlyContinue)) {
    throw 'Close Mission Planner before building. Do not replace loaded runtime binaries.'
}

$sourcePassed = $false
if ($Stage -in @('Source','Build')) {
    Write-Host '[SOURCE] Checking native Windows UI, authentication and non-actuating contracts.'
    & powershell -NoProfile -ExecutionPolicy Bypass -File (
        Join-Path $PSScriptRoot 'Test-AeroHelmWindowsPages.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Windows source contracts failed.' }
    $sourcePassed = $true
}
if ($Stage -eq 'Build') {
    Write-Host '[BUILD] Native Visual Studio build; writes local build and plugin outputs.'
    Write-Host 'This does not deploy Oracle, Pi or enable vehicle commands.'
    $buildScript = Join-Path $PSScriptRoot 'Build-Integrated-AeroHelm.ps1'
    if ($SkipBranding) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript -SkipBranding
    } else {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript
    }
    if ($LASTEXITCODE -ne 0) { throw 'Windows integrated build failed.' }
}
if ($Stage -in @('Build','Artifacts')) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'MissionPlanner.exe not present; native build has not produced an executable.'
    }
    if (-not (Test-Path -LiteralPath $plugin -PathType Leaf)) {
        throw 'AeroHelm compiled plugin not present; plugin build/install is unverified.'
    }
}
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$manifest = [ordered]@{
    schema = 'aerohelm.windows.readiness.v1'
    created_utc = [DateTime]::UtcNow.ToString('o')
    repository = 'Saitanveesh/AeroHelm-GCS'
    branch = $actualBranch
    commit = $commit
    stage = $Stage
    source_contracts_passed = $sourcePassed
    native_build_reported_success = ($Stage -eq 'Build')
    native_ui_smoke_verified = $false
    pixhawk_pi_verified = $false
    physical_flight_authorized = $false
    artifacts = @(
        (Artifact $exe 'MissionPlanner.exe')
        (Artifact $plugin 'AeroHelm.Cloud.Plugin.Validation.dll')
    )
}
$manifestPath = Join-Path $logDir 'aerohelm-windows-readiness.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Write-Host "Readiness manifest: $manifestPath"
Write-Host "Branch: $actualBranch | Commit: $commit"
Write-Host 'Remaining release gates: Windows UI smoke, plugin load and propeller-off Pi/Pixhawk bench.'
Write-Host 'Build artifacts, even if successful, do not authorize autonomous physical flight.'
