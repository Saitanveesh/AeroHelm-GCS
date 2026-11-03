# One-command preparation for the AeroHelm integrated desktop workspace.
# Not an autopilot flight or cloud deployment command.
param(
    [switch]$SkipBranding,
    [string]$LogoPath = "",
    [string]$IconPath = "",
    [switch]$Launch
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$allowedBranches = @("feat/aerohelm-touch-console", "feat/aerohelm-windows-integration", "feat/aerohelm-windows-pages-v1", "feat/aerohelm-windows-readiness-v2")
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

    Write-Host "[3/5] Full Mission Planner solution via Visual Studio MSBuild" -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File .\scripts\Build-AeroHelmGCS.ps1
    if ($LASTEXITCODE -ne 0) { throw "AeroHelm GCS full solution build failed." }

    Write-Host "[4/5] Compile and install CLOUD + SURVEY + VISION + LINK + OPS" -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File .\scripts\Install-AeroHelmCloud.ps1
    if ($LASTEXITCODE -ne 0) { throw "AeroHelm plugin installation failed." }

    if (-not $SkipBranding) {
        Write-Host "[5/5] Install AeroHelm GCS product title and optional logo" -ForegroundColor Cyan
        & powershell -ExecutionPolicy Bypass -File .\scripts\Install-AeroHelmBranding.ps1 -LogoPath $LogoPath -IconPath $IconPath
        if ($LASTEXITCODE -ne 0) { throw "Branding installation failed." }
    }
    else {
        Write-Host "[5/5] Branding skipped by operator request."
    }

    Write-Host "DESKTOP BUILD COMPLETE. Flight execution remains disabled on hardware." -ForegroundColor Green
    if ($Launch) {
        Write-Host "Launching AeroHelm GCS (startup HTTPS login when branded)..."
        $env:AEROHELM_CLOUD_READONLY = "1"
        Start-Process ".\bin\Release\net461\MissionPlanner.exe"
    }
}
finally {
    Pop-Location
}
