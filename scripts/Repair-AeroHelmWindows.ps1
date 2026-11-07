# Vihanga single-entry native Windows repair, inspection-only by default.
# No aircraft, Pi or Oracle operations. -Apply backs up the entire old GCS.
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fix = Join-Path $PSScriptRoot 'Consolidate-AeroHelmWindows.ps1'
$log = Join-Path $root 'build-logs\aerohelm-solution.log'

if (-not $Apply) {
    Write-Host 'INSPECTION ONLY. No build, deletion, or replacement.'
    & $fix -Stage Inspect
    Write-Host 'To repair/build/verify after pulling the latest branch, rerun with -Apply.'
    return
}
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close Mission Planner before repair; no running EXE/DLL is replaced.'
}
try {
    & $fix -Stage Inspect
    & $fix -Stage Build -Apply -WithBranding
    & $fix -Stage Verify
    Write-Host 'SUCCESS: full Windows GCS compilation, plugin install and hash verification passed.' -ForegroundColor Green
    Write-Host 'Next launch: powershell -File .\scripts\Start-AeroHelm-Session.ps1'
    Write-Host 'Native UI smoke and physical Pixhawk tests are still separate.'
}
catch {
    $err = $_
    Write-Warning 'WINDOWS GCS REPAIR DID NOT COMPLETE. Do not launch or delete the saved runtime.'
    if (Test-Path -LiteralPath $log -PathType Leaf) {
        $errors = @(Select-String -LiteralPath $log -Pattern ': error (CS[0-9]+|MSB[0-9]+)' |
            ForEach-Object { $_.Line.Trim() } |
            Select-Object -Unique -First 12)
        if ($errors.Count -gt 0) {
            Write-Host 'First distinct MSBuild diagnostics (full log remains on disk):' -ForegroundColor Yellow
            $errors | ForEach-Object { Write-Host $_ }
        }
    }
    Write-Host ('Detailed build log: ' + $log)
    Write-Host 'No Pi, Oracle, or Pixhawk commands were issued.'
    throw $err
}
