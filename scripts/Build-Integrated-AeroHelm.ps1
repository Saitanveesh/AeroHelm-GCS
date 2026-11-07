# One-command preparation for the Vihanga integrated ground station.
# Not an autopilot flight or cloud deployment command.
param(
    [switch]$SkipBranding,
    [string]$LogoPath = "",
    [string]$IconPath = "",
    [switch]$SkipLegacySettings,
    [switch]$Launch
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$allowedBranches = @("master", "feat/vihanga-phase3-native-toolbar", "feat/gcs-research-cybershield-twin-ui", "feat/gcs-unified-login-readonly-bridge", "feat/aerohelm-touch-console", "feat/aerohelm-windows-integration", "feat/aerohelm-windows-pages-v1", "feat/aerohelm-windows-readiness-v2", "feat/aerohelm-windows-release-candidate", "feat/aerohelm-combined-preflight")
$branch = (& git -C $root branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -notin $allowedBranches) {
    throw "Expected a reviewed AeroHelm Windows development branch; current branch $branch. Git fetch/switch manually first."
}
if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw "Close all AeroHelm/MissionPlanner windows before build."
}
Push-Location $root
try {
    Write-Host "[1/5] Windows page source-contract checks" -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
    if ($LASTEXITCODE -ne 0) { throw "Windows source-contract checks failed." }

    Write-Host "[2/5] Offline autonomous survey geometry/export assertions" -ForegroundColor Cyan
    & dotnet run --project Tools\AeroHelmSurveyTests\AeroHelmSurveyTests.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Survey geometry/export tests failed." }

    Write-Host "[2/5] Native MAVLink protocol and real attitude contract assertions" -ForegroundColor Cyan
    & dotnet run --project Tools\AeroHelmMavlinkBridgeTests\AeroHelmMavlinkBridgeTests.csproj --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Read-only MAVLink native bridge contract tests failed." }

    Write-Host "[3/5] Full Mission Planner solution via Visual Studio MSBuild" -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File .\scripts\Build-AeroHelmGCS.ps1
    if ($LASTEXITCODE -ne 0) { throw "AeroHelm GCS full solution build failed." }

    Write-Host "[4/5] Transactionally install compiled CLOUD + PATHS + SURVEY + VISION + LINK + OPS + INTEL + MORE" -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File .\scripts\Install-AeroHelmCloud.ps1
    if ($LASTEXITCODE -ne 0) { throw "AeroHelm plugin installation failed." }

    if (-not $SkipBranding) {
        Write-Host "[5/5] Install Vihanga GCS product title and optional logo" -ForegroundColor Cyan
        # PowerShell 5.1 drops empty strings when forwarding arguments to a
        # second powershell.exe process. Optional artwork must be omitted,
        # not passed as -LogoPath/-IconPath with missing values.
        $brandingParams=@{}
        if (-not [string]::IsNullOrWhiteSpace($LogoPath)) {
            $brandingParams['LogoPath']=$LogoPath
        }
        if (-not [string]::IsNullOrWhiteSpace($IconPath)) {
            $brandingParams['IconPath']=$IconPath
        }
        if ($SkipLegacySettings) {
            $brandingParams['SkipLegacySettings']=$true
        }
        # Invoke the PowerShell script directly so a path containing spaces
        # remains a single argument and terminating errors propagate.
        & (Join-Path $PSScriptRoot 'Install-AeroHelmBranding.ps1') @brandingParams
        if (-not $?) { throw "Branding installation failed." }
        $label=Join-Path $root 'bin\Release\net461\logo.txt'
        if(-not (Test-Path -LiteralPath $label -PathType Leaf) -or
           (Get-Content -LiteralPath $label -Raw).Trim() -ne 'Vihanga GCS') {
            throw 'Branding completed without the expected Vihanga GCS title.'
        }
    }
    else {
        Write-Host "[5/5] Branding skipped by operator request."
    }

    Write-Host "DESKTOP BUILD COMPLETE. Flight execution remains disabled on hardware." -ForegroundColor Green
    if ($Launch) {
        Write-Host "Launching Vihanga GCS (startup HTTPS login when branded)..."
        $env:AEROHELM_CLOUD_READONLY = "1"
        Start-Process ".\bin\Release\net461\MissionPlanner.exe"
    }
}
finally {
    Pop-Location
}
