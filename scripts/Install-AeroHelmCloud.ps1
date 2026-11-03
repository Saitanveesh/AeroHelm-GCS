# Transactional installation of the compiled AeroHelm Mission Planner plugin.
# Requires a clean, stopped Windows Mission Planner. On error restores BOTH
# the previously installed DLL and the optional legacy source plugin.
# All backups remain local under git-ignored build-logs/plugin-backups.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exeDir = Join-Path $root 'bin\Release\net461'
$exe = Join-Path $exeDir 'MissionPlanner.exe'
$pluginDir = Join-Path $exeDir 'plugins'
$assembly = 'AeroHelm.Cloud.Plugin.Validation.dll'
$target = Join-Path $pluginDir $assembly
$oldSource = Join-Path $pluginDir 'AeroHelmCloud.cs'

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "MissionPlanner.exe missing: $exe. Build the full solution first."
}
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Mission Planner is running. Close it before plugin installation.'
}

Write-Host 'Compiling AeroHelm native plugin before making runtime changes...'
& (Join-Path $PSScriptRoot 'Build-AeroHelmGCS.ps1') -Target Plugin
if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed. Existing installation unchanged.' }

$buildDir = Join-Path $root 'Plugins\AeroHelmCloud\bin'
if (-not (Test-Path -LiteralPath $buildDir -PathType Container)) {
    throw "Plugin build output directory missing: $buildDir"
}
$built = @(Get-ChildItem -LiteralPath $buildDir -Recurse -File -Filter $assembly |
    Where-Object { $_.FullName -match '[\\/]Release[\\/]' } |
    Sort-Object LastWriteTimeUtc -Descending)
if ($built.Count -lt 1) { throw 'No compiled Release plugin DLL. Existing installation unchanged.' }
$source = $built[0].FullName
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-Item -LiteralPath $source).Length -lt 1024) {
    throw 'Compiled plugin is implausibly small; refusing installation.'
}

New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
$backupRoot = Join-Path $root 'build-logs\plugin-backups'
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' +
    ([Guid]::NewGuid().ToString('N').Substring(0,8))
$backupDir = Join-Path $backupRoot $stamp
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
$hadDll = Test-Path -LiteralPath $target -PathType Leaf
$hadSource = Test-Path -LiteralPath $oldSource -PathType Leaf
$previousHash = $null
$previousSourceHash = $null
$staged = Join-Path $pluginDir ('.aerohelm-stage-' + $stamp + '.dll')
$installationStarted = $false

try {
    # Backup first, preserving a recovery path even if a later copy fails.
    if ($hadDll) {
        $previousHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        Copy-Item -LiteralPath $target -Destination (Join-Path $backupDir $assembly) -ErrorAction Stop
        $copiedHash = (Get-FileHash -LiteralPath (Join-Path $backupDir $assembly) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($copiedHash -ne $previousHash) { throw 'Existing DLL backup hash mismatch.' }
    }
    if ($hadSource) {
        $previousSourceHash = (Get-FileHash -LiteralPath $oldSource -Algorithm SHA256).Hash.ToLowerInvariant()
        Copy-Item -LiteralPath $oldSource -Destination (Join-Path $backupDir 'AeroHelmCloud.cs') -ErrorAction Stop
        $copiedSourceHash = (Get-FileHash -LiteralPath (Join-Path $backupDir 'AeroHelmCloud.cs') -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($copiedSourceHash -ne $previousSourceHash) { throw 'Legacy plugin backup hash mismatch.' }
    }
    Copy-Item -LiteralPath $source -Destination $staged -ErrorAction Stop
    $stagedHash = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($stagedHash -ne $sourceHash) { throw 'Staged plugin DLL hash mismatch.' }

    $installationStarted = $true
    Copy-Item -LiteralPath $staged -Destination $target -Force -ErrorAction Stop
    $installedHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($installedHash -ne $sourceHash) { throw 'Installed plugin DLL failed SHA-256 verification.' }

    # The old interpreted C# plugin would create duplicate toolbar instances.
    # Remove it only AFTER DLL installation succeeded and a backup exists.
    if ($hadSource) { Remove-Item -LiteralPath $oldSource -Force -ErrorAction Stop }
    if (Test-Path -LiteralPath $oldSource -PathType Leaf) {
        throw 'Duplicate legacy source plugin remains installed.'
    }

    $manifest = [ordered]@{
        schema = 'aerohelm.plugin-install.v1'
        timestamp_utc = [DateTime]::UtcNow.ToString('o')
        runtime_assembly = $assembly
        previous_dll_present = $hadDll
        previous_dll_sha256 = $previousHash
        previous_source_present = $hadSource
        previous_source_sha256 = $previousSourceHash
        new_dll_sha256 = $installedHash
        install_verified = $true
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (
        Join-Path $backupDir 'manifest.json') -Encoding UTF8 -ErrorAction Stop
    Write-Host "Installed and SHA-256 verified: $target" -ForegroundColor Green
    Write-Host "Local rollback backup: $backupDir"
    Write-Host 'To restore: scripts\Restore-AeroHelmPlugin.ps1 -BackupName <last directory name> -Apply'
}
catch {
    $failure = $_
    if ($installationStarted) {
        Write-Warning 'Plugin installation failed; attempting to restore prior runtime state.'
        try {
            if ($hadDll) {
                Copy-Item -LiteralPath (Join-Path $backupDir $assembly) -Destination $target -Force -ErrorAction Stop
                $restored = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($restored -ne $previousHash) { throw 'Previous DLL restoration hash mismatch.' }
            } elseif (Test-Path -LiteralPath $target -PathType Leaf) {
                Remove-Item -LiteralPath $target -Force -ErrorAction Stop
            }
            if ($hadSource) {
                Copy-Item -LiteralPath (Join-Path $backupDir 'AeroHelmCloud.cs') -Destination $oldSource -Force -ErrorAction Stop
            }
        }
        catch {
            Write-Error "CRITICAL: plugin rollback could not be verified. Keep Mission Planner stopped. Local backup: $backupDir"
        }
    }
    throw $failure
}
finally {
    if (Test-Path -LiteralPath $staged -PathType Leaf) {
        Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
    }
}
