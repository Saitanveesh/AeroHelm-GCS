# Offline Vihanga Phase 4 development acceptance runner.
# No aircraft, Pi or Oracle network access. NativeBuild may change local
# Windows binaries through the existing guarded installer; never launches.
[CmdletBinding()]
param([switch]$NativeBuild)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$logDirectory = Join-Path $root 'build-logs'
$started = [DateTimeOffset]::UtcNow
$gitRevision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $gitRevision -notmatch '^[a-f0-9]{40}$') {
    throw 'Cannot identify source commit for Phase 4 acceptance.'
}
$checks = New-Object System.Collections.ArrayList
function Invoke-PhaseGate([string]$name, [string]$executable, [string[]]$arguments) {
    Write-Host "CHECK $name" -ForegroundColor Cyan
    & $executable @arguments
    $exit = $LASTEXITCODE
    [void]$checks.Add([pscustomobject]@{
        name = $name; exit_code = $exit; passed = ($exit -eq 0)
    })
    if ($exit -ne 0) {
        throw "$name failed (exit code $exit). Phase 4 acceptance blocked."
    }
}
try {
    Push-Location $root
    Invoke-PhaseGate 'Windows source contracts' 'powershell.exe' @(
        '-NoProfile','-ExecutionPolicy','Bypass','-File',
        (Join-Path $PSScriptRoot 'Test-AeroHelmWindowsPages.ps1'))
    Invoke-PhaseGate 'Native workspace/toolbar inventory' 'powershell.exe' @(
        '-NoProfile','-ExecutionPolicy','Bypass','-File',
        (Join-Path $PSScriptRoot 'Audit-AeroHelmModules.ps1'))
    Invoke-PhaseGate 'Offline survey planning' 'dotnet' @(
        'run','--project','Tools/AeroHelmSurveyTests/AeroHelmSurveyTests.csproj',
        '--configuration','Release')
    Invoke-PhaseGate 'Actual C# MAVLink bridge contracts' 'dotnet' @(
        'run','--project',
        'Tools/AeroHelmMavlinkBridgeTests/AeroHelmMavlinkBridgeTests.csproj',
        '--configuration','Release')
    # Python dependencies must be provisioned in the active development venv.
    # Do not implicitly download dependencies during a test run.
    Invoke-PhaseGate 'Relay, Pi and simulation contracts' 'python' @(
        '-m','pytest','-q','services/vihanga_link')
    if ($NativeBuild) {
        Invoke-PhaseGate 'Guarded native build and hash verification' 'powershell.exe' @(
            '-NoProfile','-ExecutionPolicy','Bypass','-File',
            (Join-Path $PSScriptRoot 'Install-VihangaVerified.ps1'),'-Apply')
    }
}
finally {
    Pop-Location
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $expected = if($NativeBuild){6}else{5}
    $passed = ($checks.Count -eq $expected -and
        @($checks | Where-Object {-not $_.passed}).Count -eq 0)
    $report = [ordered]@{
        schema = 'vihanga.phase4.offline-acceptance.v1'
        source_commit = $gitRevision
        started_utc = $started.ToString('o')
        finished_utc = [DateTimeOffset]::UtcNow.ToString('o')
        checks = @($checks)
        native_build_requested = [bool]$NativeBuild
        checks_passed = $passed
        cloud_live_telemetry_verified = $false
        pixhawk_hardware_verified = $false
        physical_flight_authorized = $false
    }
    $target = Join-Path $logDirectory 'vihanga-phase4-offline-acceptance.json'
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $target -Encoding UTF8
    Write-Host "Offline acceptance report: $target"
    if (-not $passed) { Write-Warning 'Offline acceptance incomplete or failed.' }
    else { Write-Host 'Offline checks passed; live hardware gates remain separate.' }
}
