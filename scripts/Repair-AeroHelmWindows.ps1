# Vihanga Windows recovery. A real rebuild is required after a source update.
# Native Windows only; does not communicate with Pi/FC or issue vehicle commands.
param([switch]$Apply)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fix=Join-Path $PSScriptRoot 'Consolidate-AeroHelmWindows.ps1'
$logs=Join-Path $root 'build-logs'
function Invoke-FreshReleaseStep([string[]]$StepArgs) {
    # Child process exit code is authoritative. An in-process script could
    # otherwise leave LASTEXITCODE from an unrelated successful command.
    $arguments=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass',
                 '-File',$fix) + $StepArgs
    & powershell.exe @arguments
    $code=$LASTEXITCODE
    if($code -ne 0){
        throw ("Release step ["+($StepArgs -join ' ')+"] failed (exit "+$code+").")
    }
}
if(-not $Apply){
    Write-Host 'INSPECT ONLY. Use -Apply to build/install and verify the current commit.'
    Invoke-FreshReleaseStep @('Inspect')
    return
}
if(Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue){
    throw 'Close Mission Planner before repair. Running EXE/DLL files are never overwritten.'
}
try {
    Invoke-FreshReleaseStep @('Inspect')
    Write-Host 'REBUILD: prior manifest belongs to an older commit until the new native build succeeds.'
    Invoke-FreshReleaseStep @('Build','-Apply','-WithBranding')
    Invoke-FreshReleaseStep @('Verify')
    Write-Host 'SUCCESS: exact-source Windows rebuild, plugin install and hash verification passed.' -ForegroundColor Green
    Write-Host 'Launch separately with scripts/Start-VihangaGCS.ps1 -NoAutoTcp.'
    Write-Host 'Physical aircraft use still requires on-site bench acceptance.'
}
catch {
    $failure=$_
    Write-Warning 'REPAIR FAILED: installed runtime must remain stopped; previous runtime backup retained.'
    foreach($name in @('aerohelm-solution.log','aerohelm-solution-console.log',
                       'aerohelm-plugin.log','aerohelm-plugin-console.log')){
        $log=Join-Path $logs $name
        if(Test-Path -LiteralPath $log -PathType Leaf){
            Write-Host "Build log: $log"
            if($name -notlike '*-console.log'){
                $errors=@(Select-String -LiteralPath $log -Pattern 'error (CS[0-9]+|MSB[0-9]+|NU[0-9]+)' |
                    Select-Object -First 12)
                foreach($entry in $errors){ Write-Host $entry.Line }
            }
        }
    }
    Write-Host 'Release verification was not bypassed or the manifest rewritten.'
    throw $failure
}
