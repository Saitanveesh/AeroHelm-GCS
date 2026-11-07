# Vihanga GCS — clean Windows checkout and native installation

**Purpose:** replace the working-folder contents without importing old
compiler outputs, DLLs, cached build manifests or the prior window layout.
Keep the last-good installation available until the replacement has passed
actual Windows startup and passive Pi/FC acceptance.

**Prerequisites:** Windows PowerShell 5.1, Git, existing Visual Studio
MSBuild/.NET Framework 4.7.2 targeting pack and .NET SDK used by the
native tests. The Pi/FC remain disarmed with propellers removed. This
does **not** deploy or update the running Oracle M1 backend or the Pi UART
agent. M1's missing ATTITUDE frames must be diagnosed separately.

## 1. Archive the previous checkout, without deleting data

Close all Mission Planner windows and PowerShell consoles that are running
scripts inside the old checkout. Start a **new Windows PowerShell terminal**,
then execute:

~~~powershell
$ErrorActionPreference = 'Stop'
Set-Location $HOME
$old = Join-Path $HOME 'AeroHelm-GCS'
$new = Join-Path $HOME 'Vihanga-GCS'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$archive = Join-Path $HOME ("AeroHelm-GCS-previous-" + $stamp)

if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close Mission Planner before archiving its checkout.'
}
if (Test-Path -LiteralPath $new) {
    throw "Clean install target already exists: $new; do not overwrite it."
}
if (-not (Test-Path -LiteralPath $old -PathType Container)) {
    throw "Previous checkout not found: $old"
}
Rename-Item -LiteralPath $old -NewName (Split-Path -Leaf $archive)
Write-Host "Last-good checkout retained: $archive"
~~~

The external `AeroHelm-GCS-release-backups` directory is never removed.
Do not use `git reset --hard`, `git clean -fdx` or recursive deletion.

## 2. Reset *only* the Vihanga window profile, with a backup

Do not delete shared Mission Planner or AeroHelm configuration. The
first-install build now explicitly skips importing old profiles. If
`Vihanga GCS.xml` exists in Documents, move it to a separately named
backup so it cannot reinstate the previous window/tab arrangements:

~~~powershell
$settingsRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mission Planner'
$profile = Join-Path $settingsRoot 'Vihanga GCS.xml'
$profileBackup = Join-Path $HOME ('Vihanga-profile-previous-' + $stamp)
if (Test-Path -LiteralPath $profile -PathType Leaf) {
    New-Item -Path $profileBackup -ItemType Directory -Force | Out-Null
    Move-Item -LiteralPath $profile -Destination (Join-Path $profileBackup 'Vihanga GCS.xml')
    Write-Host "Profile retained in $profileBackup"
}
~~~

This is **not** a credential wipe or a modification to the aircraft.
Re-enter cloud operator login when starting the new installation.

## 3. Clone the reviewed source into a different directory

~~~powershell
git clone --recurse-submodules --shallow-submodules --single-branch --branch feat/vihanga-phase3-native-toolbar https://github.com/Saitanveesh/AeroHelm-GCS.git $new
if ($LASTEXITCODE -ne 0) { throw 'Clone failed; previous folder is still preserved.' }

Set-Location $new
git status --short
git branch --show-current
git rev-parse HEAD
~~~

A fresh clone has **no compiled EXE/DLL** and **no release manifest**.
Never copy `bin/Release/net461`, `build-logs`, old `plugins`
or build-output `obj` directories from the archived checkout.

## 4. First-install build and SHA-256 verification

The source now supports a distinct first-install transaction. It refuses
an existing runtime and skips legacy profile migration:

~~~powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
if ($LASTEXITCODE -ne 0) { throw 'Source checks failed before build.' }

powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Bootstrap -Apply -WithBranding
if ($LASTEXITCODE -ne 0) { throw 'Clean build failed. Do not launch or delete the last-good checkout.' }

powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Verify
if ($LASTEXITCODE -ne 0) { throw 'Installed EXE/DLL verification failed. Last-good checkout retained.' }
~~~

After successful bootstrap, use `scripts/Update-AeroHelmLatest.ps1 -Apply`
for future changes; Bootstrap refuses to replace an installed runtime.

## 5. Windows UI and passive hardware acceptance

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 -NoAutoTcp
~~~

Check: the native FLIGHT action tabs form **one row**; QuickView is
scrollable if needed; PATHS shows **three dark selectable cards**, not
green blocks; the toolbar defaults to **CLOUD, PATHS, MORE** with all
secondary tools under MORE; LINK defaults to a short status, with a
`Technical details` option. Windows DPI and narrow-window resize must
be checked on the actual PC.

A real native HUD requires fresh **MAVLink ID 0 HEARTBEAT** and
**ID 30 ATTITUDE**. An empty/stationary horizon is *not* a valid
attitude test even if M1 receives other frames. Cloud status and simulated
reporting do not authorize a physical flight.

## 6. Deleting the previous folder is optional and deferred

Only after the new executable and plugin hashes verify, the Windows UI
passes and the required old logs/settings have been retained should the
operator remove the archived folder. The default procedure leaves it
untouched so a previously working runtime can be recovered if the
new clean-build or cloud source proves incompatible.

**This procedure never deletes your old checkout or operator data.**
