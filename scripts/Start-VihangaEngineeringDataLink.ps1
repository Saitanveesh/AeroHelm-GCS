# Isolated Windows engineering-data-link build/launch. Does not change verified master.
# Cloud connection is strictly receive-only; no ARM, PARAM_SET or mission upload.
[CmdletBinding()]
param(
    [string]$LinkOrigin = 'https://aerohelm-vcn.tailf3ab6a.ts.net:8443',
    [string]$OracleInstanceOcid = '',
    [string]$VehicleId = 'AH-HW-001',
    [switch]$Build,
    [switch]$Launch
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$expected = 'feat/engineering-datalink-native-bind-20261109'
$branch = (& git -C $root branch --show-current).Trim()
$sha = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -cne $expected -or $sha -notmatch '^[0-9a-f]{40}$') {
    throw "Open the separate $expected worktree, not the installed master checkout."
}
if (@(& git -C $root status --porcelain --untracked-files=no).Count -ne 0) {
    throw 'Tracked changes present; verify or commit them before building.'
}
$uri = $null
if (-not [Uri]::TryCreate($LinkOrigin, [UriKind]::Absolute, [ref]$uri) -or
    $uri.Scheme -ne 'https' -or $uri.Host -notlike '*.ts.net' -or
    $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/' -or
    $uri.Port -ne 8443) {
    throw 'Use the verified private *.ts.net HTTPS relay origin on port 8443.'
}
if (-not $OracleInstanceOcid) { $OracleInstanceOcid = $env:VIHANGA_EXPECTED_ORACLE_INSTANCE_OCID }
if ($OracleInstanceOcid -notmatch '^ocid1[.]instance[.]oc[0-9]+[.][A-Za-z0-9_.-]{24,}$') {
    throw 'Provide the full Oracle Compute instance OCID for target identity pinning.'
}
if ($VehicleId -notmatch '^[A-Za-z0-9_-]{3,64}$') { throw 'Invalid aircraft identifier.' }
if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close all Mission Planner instances before an isolated native build or launch.'
}
$exe = Join-Path $root 'bin\Release\net461\MissionPlanner.exe'
$dll = Join-Path $root 'bin\Release\net461\plugins\AeroHelm.Cloud.Plugin.Validation.dll'
$manifestFile = Join-Path $root 'build-logs\engineering-datalink-build.json'

if ($Build) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Test-AeroHelmWindowsPages.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Native source safety contract failed.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Build-AeroHelmGCS.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Full Mission Planner build failed.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Install-AeroHelmCloud.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Transactional cloud plugin install failed.' }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw 'New executable or cloud plugin DLL missing after build.'
    }
    $after = (& git -C $root rev-parse HEAD).Trim()
    if ($after -cne $sha) { throw 'Source commit changed during build; refusing manifest.' }
    $manifest = [pscustomobject]@{
        schema='vihanga.engineering-datalink.v1'
        git_sha=$sha
        branch=$branch
        exe_sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        plugin_sha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $folder = Split-Path -Parent $manifestFile
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestFile -Encoding UTF8
    Write-Output 'Engineering native build completed and source/artifact hashes recorded.'
}

if ($Launch) {
    if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
        throw 'No source-matched engineering build manifest. Run -Build first.'
    }
    $build = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($build.schema -ne 'vihanga.engineering-datalink.v1' -or
        $build.branch -cne $expected -or $build.git_sha -cne $sha -or
        -not (Test-Path -LiteralPath $exe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dll -PathType Leaf) -or
        $build.exe_sha256 -cne (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant() -or
        $build.plugin_sha256 -cne (Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Native source or artifacts changed since the verified engineering build.'
    }
    $listeners = @(Get-NetTCPConnection -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue)
    if ($listeners.Count -gt 0) {
        throw 'Loopback TCP 5760 is already occupied. Inspect the owner; no process is stopped automatically.'
    }
    $origin = $uri.GetLeftPart([UriPartial]::Authority)
    $env:VIHANGA_LINK_ORIGIN = $origin
    $env:VIHANGA_EXPECTED_ORACLE_INSTANCE_OCID = $OracleInstanceOcid
    $env:AEROHELM_CLOUD_URL = $origin
    $env:AEROHELM_VEHICLE_ID = $VehicleId
    $env:AEROHELM_REQUIRE_LOGIN = '0'
    $env:AEROHELM_CLOUD_READONLY = '1'
    $env:AEROHELM_INTERNAL_TCP_BRIDGE = '1'
    $env:AEROHELM_AUTO_NATIVE_TCP = '0'
    $env:AEROHELM_REDUCED_TOOLBAR = '1'
    Write-Output 'Launching source-matched engineering GCS. In TOOLS select Open cloud Flight Data (HUD + GPS map).'
    Write-Output 'Relay operator sign-in is separate and prompts within the app. Raw GCS uplink remains discarded.'
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
}
if (-not $Build -and -not $Launch) {
    Write-Output 'No files changed. Use -Build, then -Launch, or -Build -Launch for the isolated engineering viewer.'
}
