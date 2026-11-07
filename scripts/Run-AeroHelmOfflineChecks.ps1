# Read-only source and protocol self-tests; no Pixhawk, Oracle login or Pi.
# This is NOT native Windows compilation or hardware flight acceptance.
param([switch]$SkipDotNet)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) {
        throw 'Python 3 is required for the offline source regression suite.'
    }
    Write-Host '[1/3] Checking read-only GCS startup, icons, flight and data contracts'
    & $python.Source -m unittest -v Tools.test_aerohelm_offline_gcs Tools.test_aerohelm_build_globs Tools.test_vihanga_native_toolbar
    if ($LASTEXITCODE -ne 0) { throw 'Offline GCS source contracts failed.' }

    if ($SkipDotNet) {
        Write-Host '[2/3] Native MAVLink bridge assertions SKIPPED (explicit)'
        Write-Host '[3/3] Offline survey geometry tests SKIPPED (explicit)'
    }
    else {
        $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $dotnet) {
            throw 'Install .NET 8 SDK to run the actual linked C# receiver and survey tests, or use -SkipDotNet for source-only checks.'
        }
        Write-Host '[2/3] Compile actual native receive-only MAVLink bridge and test real ATTITUDE parser'
        & $dotnet.Source run --project Tools/AeroHelmMavlinkBridgeTests/AeroHelmMavlinkBridgeTests.csproj --configuration Release
        if ($LASTEXITCODE -ne 0) { throw 'Native MAVLink bridge contract tests failed.' }
        Write-Host '[3/3] Offline mission survey generator geometry regressions'
        & $dotnet.Source run --project Tools/AeroHelmSurveyTests/AeroHelmSurveyTests.csproj --configuration Release
        if ($LASTEXITCODE -ne 0) { throw 'Offline survey regressions failed.' }
    }
    Write-Host 'PASS: hardware-free checks completed; native Windows MSBuild and physical HUD remain unverified.'
}
finally {
    Pop-Location
}
