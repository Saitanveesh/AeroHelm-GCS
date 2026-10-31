# Build AeroHelm-GCS using the Visual Studio 2022 .NET Framework MSBuild engine.
# This is required for legacy COM references and WinForms resources.
# Usage: .\scripts\Build-AeroHelmGCS.ps1
#        .\scripts\Build-AeroHelmGCS.ps1 -Target Plugin
param(
    [ValidateSet("Solution", "Plugin")]
    [string]$Target = "Solution"
)
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$vswhere = Join-Path "${env:ProgramFiles(x86)}" "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "Visual Studio Installer / vswhere.exe not found. Install Visual Studio 2022 with MSBuild and .NET desktop development."
}

$vsLocation = (& $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($vsLocation)) {
    throw "Visual Studio 2022 MSBuild is not installed. Use Visual Studio Installer to add the .NET desktop development workload."
}
$msbuild = Join-Path $vsLocation "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    throw "Visual Studio .NET Framework MSBuild.exe was not found at: $msbuild"
}

# Don't build into an active Mission Planner runtime: its plugin DLLs can be locked.
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw "MissionPlanner.exe is running. Close it before rebuilding so plugin DLLs can be copied."
}
$project = if ($Target -eq "Plugin") {
    Join-Path $repoRoot "Plugins\AeroHelmCloud\AeroHelmCloud.Plugin.csproj"
} else {
    Join-Path $repoRoot "MissionPlanner.sln"
}
if (-not (Test-Path $project)) { throw "Build target missing: $project" }

$logFolder = Join-Path $repoRoot "build-logs"
New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
$logPath = Join-Path $logFolder ("aerohelm-" + $Target.ToLowerInvariant() + ".log")

Write-Host "Builder: $msbuild"
Write-Host "Target: $project"
Write-Host "Full log: $logPath"
Write-Host "Serial build is enabled to reduce concurrent DLL copy conflicts."

Push-Location $repoRoot
try {
    & $msbuild $project /restore /m:1 /nr:false /p:Configuration=Release /verbosity:minimal /nologo "/flp:LogFile=$logPath;Verbosity=normal" /clp:ErrorsOnly
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($code -ne 0) {
    Write-Host "Build failed. Share only the error lines from: $logPath" -ForegroundColor Red
    if (Test-Path $logPath) {
        Get-Content $logPath | Select-String " : error " | Select-Object -Last 30
    }
    exit $code
}
Write-Host "Build completed successfully. Review security/dependency warnings in $logPath." -ForegroundColor Green
