# Restore an AeroHelm plugin runtime snapshot created by Install-AeroHelmCloud.
# Default is INSPECT ONLY. -Apply performs the rollback. Never reads secrets.
param(
    [Parameter(Mandatory=$true)]
    [string]$BackupName,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
if ($BackupName -notmatch '^[0-9]{8}-[0-9]{6}-[0-9a-fA-F]{8}$') {
    throw 'BackupName must be an exact installer backup folder name.'
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$backup = Join-Path (Join-Path $root 'build-logs\plugin-backups') $BackupName
$manifestFile = Join-Path $backup 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
    throw 'No verified installation manifest found. Refusing an untrusted rollback.'
}
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'aerohelm.plugin-install.v1' -or
    -not $manifest.install_verified -or
    $manifest.runtime_assembly -ne 'AeroHelm.Cloud.Plugin.Validation.dll') {
    throw 'Incompatible or unverified plugin backup manifest.'
}
$pluginDir = Join-Path $root 'bin\Release\net461\plugins'
$target = Join-Path $pluginDir 'AeroHelm.Cloud.Plugin.Validation.dll'
$legacy = Join-Path $pluginDir 'AeroHelmCloud.cs'
$dllBackup = Join-Path $backup 'AeroHelm.Cloud.Plugin.Validation.dll'
$sourceBackup = Join-Path $backup 'AeroHelmCloud.cs'

function Assert-PreviousHash([bool]$expected,[string]$path,[string]$hash) {
    if (-not $expected) { return }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Backup file missing: $path"
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $hash) { throw "Backup content hash mismatch: $path" }
}
Assert-PreviousHash ([bool]$manifest.previous_dll_present) $dllBackup $manifest.previous_dll_sha256
Assert-PreviousHash ([bool]$manifest.previous_source_present) $sourceBackup $manifest.previous_source_sha256

Write-Host "Rollback snapshot: $BackupName"
Write-Host "Previous compiled plugin present: $($manifest.previous_dll_present)"
Write-Host "Previous dynamic C# plugin present: $($manifest.previous_source_present)"
if (-not $Apply) {
    Write-Host 'INSPECT ONLY: nothing changed. Re-run with -Apply to restore this verified snapshot.'
    return
}
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close Mission Planner before changing plugin binaries.'
}
if (-not (Test-Path -LiteralPath $pluginDir -PathType Container)) {
    throw 'Plugin target directory missing; refusing implicit restore to a different location.'
}
# Restore the DLL first; on failure do not delete any other runtime files.
if ($manifest.previous_dll_present) {
    Copy-Item -LiteralPath $dllBackup -Destination $target -Force -ErrorAction Stop
    $restored = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($restored -ne $manifest.previous_dll_sha256) {
        throw 'Restored DLL failed SHA-256 verification.'
    }
} elseif (Test-Path -LiteralPath $target -PathType Leaf) {
    Remove-Item -LiteralPath $target -Force -ErrorAction Stop
}
if ($manifest.previous_source_present) {
    Copy-Item -LiteralPath $sourceBackup -Destination $legacy -Force -ErrorAction Stop
    $restoredSource = (Get-FileHash -LiteralPath $legacy -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($restoredSource -ne $manifest.previous_source_sha256) {
        throw 'Restored source plugin failed SHA-256 verification.'
    }
} elseif (Test-Path -LiteralPath $legacy -PathType Leaf) {
    Remove-Item -LiteralPath $legacy -Force -ErrorAction Stop
}
Write-Host 'Rollback completed; saved manifest and backup retained.' -ForegroundColor Green
Write-Host 'Start Mission Planner only after confirming the expected plugin count.'
