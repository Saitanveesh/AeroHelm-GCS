# Unified offline source + read-only cloud acceptance for Vihanga.
# Never arms, disarms, uploads missions, edits params or restarts services.
[CmdletBinding()]
param(
    [string]$LinkOrigin = "https://aerohelm-vcn.tailf3ab6a.ts.net:8443",
    [string]$ExpectedBranch = "release/vihanga-integrated-bench-candidate-20261108",
    [switch]$SkipPython,
    [switch]$SkipCloud
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$head = (& git -C $root rev-parse --abbrev-ref HEAD).Trim()
$sha = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedBranch) {
    throw "Incorrect candidate checkout: $head (expected $ExpectedBranch)."
}
Write-Output "VIHANGA_CANDIDATE branch=$head sha=$sha"
$changes = @(& git -C $root status --porcelain)
if ($changes.Count -gt 0) {
    throw "Uncommitted checkout changes; use a separate clean candidate worktree."
}

# Verify native bridge source remains receive-only while typed control is gated.
& (Join-Path $root "scripts\Test-AeroHelmWindowsPages.ps1")
if ($LASTEXITCODE -ne 0) { throw "Windows source contracts failed." }
foreach ($script in @(
    "scripts\Request-VihangaTypedArmDisarm.ps1",
    "scripts\Test-VihangaControlTransport.ps1",
    "scripts\Start-VihangaGCS.ps1"
)) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $root $script), [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) { throw "PowerShell syntax failed: $script; $($errors[0].Message)" }
}
Write-Output "PASS: Source contracts and PowerShell parsing; no live commands."

if (-not $SkipPython) {
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
        throw "Python missing. Install dependencies or pass -SkipPython without claiming test pass."
    }
    Push-Location $root
    try {
        & python -m compileall -q services/vihanga_link
        if ($LASTEXITCODE -ne 0) { throw "Python syntax compile failed." }
        $tests = @(
          "services/vihanga_link/test_fc_readiness.py",
          "services/vihanga_link/test_typed_downlink.py",
          "services/vihanga_link/test_control_policy.py",
          "services/vihanga_link/test_control_api.py",
          "services/vihanga_link/test_control_transport_probe.py",
          "services/vihanga_link/test_pi_control_adapter.py",
          "services/vihanga_link/test_mission_transaction.py",
          "services/vihanga_link/test_parameter_transaction.py",
          "services/vihanga_link/test_link.py",
          "services/vihanga_link/test_deploy_contracts.py"
        )
        & python -m pytest -q @tests
        if ($LASTEXITCODE -ne 0) { throw "Offline regression tests failed." }
    } finally { Pop-Location }
    Write-Output "PASS: Offline Python tests only. Not aircraft acceptance."
} else {
    Write-Warning "Python tests SKIPPED. Source cannot be called validated."
}

if (-not $SkipCloud) {
    $uri = $null
    if (-not [Uri]::TryCreate($LinkOrigin, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne "https" -or
        -not $uri.Host.EndsWith(".ts.net", [StringComparison]::OrdinalIgnoreCase) -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne "/") {
        throw "LinkOrigin must be the exact private HTTPS Tailscale relay origin."
    }
    $origin = $uri.GetLeftPart([UriPartial]::Authority)
    $health = Invoke-RestMethod -Uri "$origin/health" -TimeoutSec 10
    if ($health.service -ne "vihanga-private-relay") {
        throw "Endpoint is not the expected Vihanga relay."
    }
    $link = Invoke-RestMethod -Uri "$origin/api/v1/mavlink/health" -TimeoutSec 10
    $control = Invoke-RestMethod -Uri "$origin/api/v1/flight-control/status" -TimeoutSec 10
    Write-Output "Live relay: $origin"
    Write-Output "Pi online: $($link.device_online) | Heartbeat age: $($link.heartbeat_age_ms) ms | Mode: $($control.mode)"
    Write-Output "Physical dispatch enabled: $($control.physical_command_dispatch)"
    if ($null -ne $control.fc_safety_report_age_ms) {
        Write-Output "FC evidence age: $($control.fc_safety_report_age_ms) ms"
    }
    if (-not $link.device_online) {
        Write-Warning "Pi disconnected: this is NOT flight ready."
    }
}
Write-Output "DONE: No motor actuation, flight commands or service deployment performed."
