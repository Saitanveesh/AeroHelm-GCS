# Destructive-but-isolated CI smoke test for plugin install + rollback.
# NEVER run against an operator computer, existing mission planner, or Pi.
# This script refuses execution unless explicitly invoked on GitHub Actions.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:AEROHELM_DISPOSABLE_CI -ne '1') {
    throw 'Disposable CI only. Cannot run on an operator machine.'
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workspace = (Resolve-Path $env:GITHUB_WORKSPACE).Path
if (-not [string]::Equals($root,$workspace,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'CI checkout mismatch; refusing to modify plugin directory.'
}
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Unexpected running Mission Planner process in CI.'
}
$pluginDir = Join-Path $root 'bin\Release\net461\plugins'
$exe = Join-Path $root 'bin\Release\net461\MissionPlanner.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'Full native Windows build must complete before CI plugin transaction test.'
}
New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
$dest = Join-Path $pluginDir 'AeroHelm.Cloud.Plugin.Validation.dll'
$legacy = Join-Path $pluginDir 'AeroHelmCloud.cs'
if ((Test-Path -LiteralPath $dest) -or (Test-Path -LiteralPath $legacy)) {
    throw 'CI plugin directory is not empty for this plugin; refuse overwriting a preexisting artifact.'
}
# Seed intentionally non-executable OLD version data in disposable CI only.
# Installer must back up the *exact bytes* and restore them after testing.
$oldBytes = New-Object byte[] 2048
for ($i=0; $i -lt $oldBytes.Length; $i++) { $oldBytes[$i] = [byte]($i % 251) }
[IO.File]::WriteAllBytes($dest,$oldBytes)
[IO.File]::WriteAllText($legacy,'// CI-only legacy plugin marker', [Text.Encoding]::UTF8)
$beforeDll = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
$beforeSource = (Get-FileHash -LiteralPath $legacy -Algorithm SHA256).Hash.ToLowerInvariant()
$backupRoot = Join-Path $root 'build-logs\plugin-backups'
try {
    # First force the verified DLL copy to fail AFTER the replacement, then
    # prove the installer restored the old binary and source automatically.
    $injectedFailureObserved = $false
    $env:AEROHELM_TEST_FAIL_AFTER_COPY = '1'
    try {
        & (Join-Path $PSScriptRoot 'Install-AeroHelmCloud.ps1')
    }
    catch {
        $injectedFailureObserved =
            $_.Exception.Message.Contains('INJECTED CI FAILURE AFTER DLL INSTALL')
    }
    finally {
        Remove-Item Env:AEROHELM_TEST_FAIL_AFTER_COPY -ErrorAction SilentlyContinue
    }
    if (-not $injectedFailureObserved) {
        throw 'Expected injected installation failure was not observed.'
    }
    if ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant() -ne $beforeDll) {
        throw 'Automatic installer error rollback did not restore previous DLL bytes.'
    }
    if (-not (Test-Path -LiteralPath $legacy -PathType Leaf) -or
        (Get-FileHash -LiteralPath $legacy -Algorithm SHA256).Hash.ToLowerInvariant() -ne $beforeSource) {
        throw 'Automatic installer error rollback did not restore previous C# plugin.'
    }

    # Now test normal installation, inspect-only, and explicit manual restore.
    & (Join-Path $PSScriptRoot 'Install-AeroHelmCloud.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Plugin installer reported a nonzero exit status.' }
    if (-not (Test-Path -LiteralPath $dest -PathType Leaf)) {
        throw 'Installed DLL missing.'
    }
    if (Test-Path -LiteralPath $legacy -PathType Leaf) {
        throw 'Legacy source was not removed after successful compiled install.'
    }
    $afterDll = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($afterDll -eq $beforeDll) { throw 'New compiled DLL did not replace CI sentinel.' }
    # The injected failed transaction may have an incomplete snapshot with
    # no manifest. Only a committed, verified install is restorable.
    $backups = @(Get-ChildItem -LiteralPath $backupRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json') } |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($backups.Count -ne 1) {
        throw "Expected one fully verified CI-local plugin backup, found $($backups.Count)."
    }
    $name = $backups[0].Name
    & (Join-Path $PSScriptRoot 'Restore-AeroHelmPlugin.ps1') -BackupName $name
    if ($LASTEXITCODE -ne 0) { throw 'Rollback inspection failed.' }
    # Inspect-only must leave the newly installed DLL unchanged.
    if ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant() -ne $afterDll) {
        throw 'Rollback inspection mode unexpectedly modified the plugin.'
    }
    & (Join-Path $PSScriptRoot 'Restore-AeroHelmPlugin.ps1') -BackupName $name -Apply
    if ($LASTEXITCODE -ne 0) { throw 'Explicit plugin rollback failed.' }
    if (-not (Test-Path -LiteralPath $legacy -PathType Leaf)) {
        throw 'Rollback failed to restore legacy plugin.'
    }
    if ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant() -ne $beforeDll) {
        throw 'Rollback failed to restore old compiled DLL bytes.'
    }
    if ((Get-FileHash -LiteralPath $legacy -Algorithm SHA256).Hash.ToLowerInvariant() -ne $beforeSource) {
        throw 'Rollback failed to restore old dynamic C# bytes.'
    }
    Write-Host 'PASS: injected automatic failure rollback, verified install, old-source removal, inspect-only mode, and manual rollback.'
}
finally {
    # Worktree is ephemeral CI. Never leave sentinel plugins in the artifact output.
    Remove-Item -LiteralPath $dest -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $legacy -Force -ErrorAction SilentlyContinue
}
