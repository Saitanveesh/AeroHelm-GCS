# Build and install AeroHelm Cloud as a compiled DLL plugin.
# Avoids Mission Planner's startup Roslyn compiler, allowing deterministic
# full-framework MSBuild diagnostics and a single runtime plugin instance.
# Run with Mission Planner closed:
#   powershell -ExecutionPolicy Bypass -File .\scripts\Install-AeroHelmCloud.ps1

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$exeDir = Join-Path $repoRoot "bin\Release\net461"
$exe = Join-Path $exeDir "MissionPlanner.exe"
$pluginDir = Join-Path $exeDir "plugins"
$assembly = "AeroHelm.Cloud.Plugin.Validation.dll"
$oldSource = Join-Path $pluginDir "AeroHelmCloud.cs"

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "MissionPlanner.exe not found: $exe. Build the full solution first."
}
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw "Mission Planner is running. Close it completely before plugin installation."
}

Write-Host "Compiling AeroHelm Cloud DLL against the local Mission Planner build..."
& (Join-Path $PSScriptRoot "Build-AeroHelmGCS.ps1") -Target Plugin
if ($LASTEXITCODE -ne 0) {
    throw "Plugin build failed (exit code $LASTEXITCODE)."
}

$pluginBuildDir = Join-Path $repoRoot "Plugins\AeroHelmCloud\bin"
if (-not (Test-Path -LiteralPath $pluginBuildDir)) {
    throw "Plugin build output directory not found: $pluginBuildDir"
}
$matches = @(Get-ChildItem -LiteralPath $pluginBuildDir -Recurse -File -Filter $assembly |
    Where-Object { $_.FullName -match '[\\/]Release[\\/]' } |
    Sort-Object LastWriteTimeUtc -Descending)
if ($matches.Count -eq 0) {
    throw "Compiled plugin DLL not found under $pluginBuildDir"
}
$plugin = $matches[0]
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
$dest = Join-Path $pluginDir $assembly
Copy-Item -LiteralPath $plugin.FullName -Destination $dest -Force

# Older instructions installed the .cs source at runtime. Do not load both
# the dynamic C# plugin and the compiled DLL, or the toolbar may duplicate.
if (Test-Path -LiteralPath $oldSource) {
    Remove-Item -LiteralPath $oldSource -Force
    Write-Host "Removed previous dynamic C# source plugin from runtime (not repository)."
}

if (-not (Test-Path -LiteralPath $dest -PathType Leaf)) {
    throw "Plugin installation did not produce the expected DLL: $dest"
}
Write-Host "Installed: $dest" -ForegroundColor Green
Write-Host "Start: $exe"
Write-Host "Expected: CLOUD toolbar button immediately after HELP."
Write-Host "If missing: Ctrl+P -> Plugins -> Errors, or check Disabled plugins and restart."
