# Vihanga engineering telemetry viewer. Separate worktree, no edits to installed master.
# Build is offline; launch requires the pinned private Oracle relay.
# Cloud native MAVLink is RECEIVE-ONLY. No flight/parameter writes are forwarded.
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
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$expectedBranch = 'feat/engineering-datalink-native-bind-20261109'
$branch = (& git -C $root branch --show-current).Trim()
$sourceSha = (& git -C $root rev-parse HEAD).Trim()

if ($LASTEXITCODE -ne 0 -or $branch -cne $expectedBranch -or
    $sourceSha -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Run this from the separate $expectedBranch worktree, not the installed master."
}
$changes = @(& git -C $root status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $changes.Count -gt 0) {
    throw 'Tracked source files are modified; review them before an engineering build.'
}
if (-not $Build -and -not $Launch) {
    Write-Output 'Use -Build for offline compilation or -Build -Launch for the cloud viewer.'
    return
}
if ($VehicleId -notmatch '^[A-Za-z0-9_-]{3,64}$') {
    throw 'Invalid aircraft identifier.'
}
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close all Mission Planner windows before building or launching.'
}

$uri = $null
if ($Launch) {
    if (-not [Uri]::TryCreate($LinkOrigin, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or
        -not $uri.Host.EndsWith('.ts.net', [StringComparison]::OrdinalIgnoreCase) -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        $uri.AbsolutePath -ne '/' -or $uri.Port -ne 8443) {
        throw 'Provide the exact private *.ts.net HTTPS relay origin on port 8443.'
    }
    if ([string]::IsNullOrWhiteSpace($OracleInstanceOcid)) {
        $OracleInstanceOcid = $env:VIHANGA_EXPECTED_ORACLE_INSTANCE_OCID
    }
    if ([string]::IsNullOrWhiteSpace($OracleInstanceOcid) -or
        $OracleInstanceOcid -notmatch '^ocid1[.]instance[.]oc[0-9]+[.][A-Za-z0-9_.-]{24,}$') {
        throw 'For -Launch supply the actual Oracle Compute instance OCID via -OracleInstanceOcid. Offline -Build needs no OCID.'
    }
}

$exe = Join-Path $root 'bin\Release\net461\MissionPlanner.exe'
$dll = Join-Path $root 'bin\Release\net461\plugins\AeroHelm.Cloud.Plugin.Validation.dll'
$manifestFile = Join-Path $root 'build-logs\engineering-datalink-build.json'

if ($Build) {
    # This worktree may have been added without initializing Git submodules.
    # The Solution includes three Mono projects, pinned by its ExtLibs/mono gitlink.
    # Initialize ONLY an absent submodule. Never reset a modified or wrong-revision
    # existing submodule: that requires an explicit operator decision.
    $monoState = @(& git -C $root submodule status -- ExtLibs/mono)
    if ($LASTEXITCODE -ne 0 -or $monoState.Count -ne 1) {
        throw 'Cannot inspect the tracked ExtLibs/mono Git submodule.'
    }
    $moduleLine = ([string]$monoState[0]).TrimEnd()
    if ($moduleLine.StartsWith('-')) {
        Write-Output 'Initializing pinned ExtLibs/mono shallow submodule for native Windows build.'
        & git -C $root submodule update --init --depth 1 -- ExtLibs/mono
        if ($LASTEXITCODE -ne 0) {
            throw 'Mono submodule checkout failed. Verify GitHub access and rerun git submodule update --init --depth 1 -- ExtLibs/mono.'
        }
    } elseif ($moduleLine.StartsWith('+') -or $moduleLine.StartsWith('U')) {
        throw 'ExtLibs/mono is at a different or conflicted revision. Inspect it manually; the launcher will not overwrite it.'
    }
    $monoPinned = (& git -C $root rev-parse 'HEAD:ExtLibs/mono').Trim()
    $monoCheckedOut = (& git -C (Join-Path $root 'ExtLibs/mono') rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $monoPinned -cne $monoCheckedOut) {
        throw 'ExtLibs/mono does not match the precise commit pinned by Vihanga source.'
    }
    $monoRequiredProjects = @(
        'ExtLibs/mono/mcs/class/System.Windows.Forms/System.Windows.Forms-net_4_x.csproj',
        'ExtLibs/mono/mcs/class/Accessibility/Accessibility-net_4_x.csproj',
        'ExtLibs/mono/mcs/class/System.Runtime.Serialization.Formatters.Soap/System.Runtime.Serialization.Formatters.Soap-net_4_x.csproj'
    )
    foreach ($relative in $monoRequiredProjects) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
            throw "Required Mono project still missing after pinned checkout: $relative"
        }
    }
    Write-Output 'Pinned Mono submodule project files: verified.'
    $contractScript = Join-Path $root 'scripts\Test-AeroHelmWindowsPages.ps1'
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $contractScript
    if ($LASTEXITCODE -ne 0) {
        throw 'Offline Windows source acceptance failed. No runtime launch attempted.'
    }

    if (-not (Get-Command 'dotnet' -ErrorAction SilentlyContinue)) {
        throw '.NET 8 SDK is needed for the native MAVLink bridge regression tests.'
    }
    $bridgeProject = Join-Path $root 'Tools\AeroHelmMavlinkBridgeTests\AeroHelmMavlinkBridgeTests.csproj'
    & dotnet run --project $bridgeProject --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw 'Native bridge MAVLink regression failed. No runtime launch attempted.'
    }

    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Build-AeroHelmGCS.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Full Visual Studio MSBuild of Mission Planner failed.'
    }

    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\Install-AeroHelmCloud.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw 'Native cloud plugin install failed.'
    }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw 'Mission Planner EXE or compiled cloud plugin DLL missing.'
    }
    $builtFrom = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $builtFrom -cne $sourceSha) {
        throw 'Git source changed during compilation; build cannot be verified.'
    }

    $manifest = [pscustomobject]@{
        schema = 'vihanga.engineering-datalink.v1'
        branch = $expectedBranch
        git_sha = $sourceSha
        exe_sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        plugin_sha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $manifestFile) | Out-Null
    $manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestFile -Encoding UTF8
    Write-Output 'Build completed. Source, EXE and native cloud DLL hashes recorded.'
}

if ($Launch) {
    if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf) -or
        -not (Test-Path -LiteralPath $exe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw 'No installed source-matched engineering build. Run -Build first.'
    }
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($manifest.schema -cne 'vihanga.engineering-datalink.v1' -or
        $manifest.branch -cne $expectedBranch -or
        $manifest.git_sha -cne $sourceSha -or
        $manifest.exe_sha256 -cne (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() -or
        $manifest.plugin_sha256 -cne (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Installed EXE/plugin hashes or source revision do not match the verified build.'
    }
    $portUsers = @(Get-NetTCPConnection -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue)
    if ($portUsers.Count -gt 0) {
        throw '127.0.0.1:5760 is already occupied; investigate the listener without killing it.'
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

    Write-Output 'Launching verified Vihanga engineering viewer (native cloud data is receive-only).'
    Write-Output 'Use TOOLS > Open cloud Flight Data (HUD + GPS map), then sign in to your telemetry relay.'
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)
}
