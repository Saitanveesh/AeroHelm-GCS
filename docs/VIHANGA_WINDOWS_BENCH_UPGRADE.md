# VIHANGA — Windows upgrade, disk cleanup, and passive bench test

**Source:** Saitanveesh/AeroHelm-GCS.
**Native GCS branch:** feat/vihanga-phase3-native-toolbar.
**Cloud staging branch:** feat/cloud-oci-private-edge-r2 (separate backend).
**Status:** Development/bench acceptance candidate, not flight-certified.

The GCS branch retains the inherited Mission Planner sources. The master
branch is not the source of the Phase-3 Windows runtime until a separate
tested integration is completed. Never treat a closed draft pull request as
proof of Windows, Pi or flight validation.

## Safety before touching the PC or aircraft

Keep the FC **disarmed**, remove propellers for bench work, make the
hardware pilot available, and preserve the configured RC/autopilot failsafes.
Close Mission Planner and its former Python bridge in their own windows;
do **not** force-kill an unknown listener or unplug the active Pi UART.
The steps below never send an arming, calibration, mission-upload or
parameter-write command. Opening a native direct COM/radio connection
later is a separate pilot-authorized operation.

Run in **Windows PowerShell** with Git, Python 3, .NET 8 SDK and Visual
Studio .NET desktop build tools available. The original GCS folder is
assumed to be C:\Users\saita\AeroHelm-GCS; change it if moved.

## 1. Fetch the exact source without discarding local changes

~~~powershell
$ErrorActionPreference = 'Stop'
$repo = 'C:\Users\saita\AeroHelm-GCS'
$branch = 'feat/vihanga-phase3-native-toolbar'
if (!(Test-Path -LiteralPath (Join-Path $repo '.git'))) { throw 'Git checkout not found.' }
if (Get-Process -Name MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close all Mission Planner/GCS windows normally before upgrading.'
}
Set-Location -LiteralPath $repo
if (@(& git status --porcelain --untracked-files=no).Count -gt 0) {
    throw 'Tracked local edits found. Preserve/commit them; do not reset or clean.'
}
& git fetch --prune origin
if ($LASTEXITCODE -ne 0) { throw 'Git fetch failed.' }
& git show-ref --verify --quiet "refs/heads/$branch"
if ($LASTEXITCODE -eq 0) { & git switch $branch }
else { & git switch --track "origin/$branch" }
if ($LASTEXITCODE -ne 0) { throw 'Unable to switch to the Vihanga branch.' }
& git pull --ff-only origin $branch
if ($LASTEXITCODE -ne 0) { throw 'Fast-forward failed. Local history retained.' }
& git submodule update --init --recursive
if ($LASTEXITCODE -ne 0) { throw 'Pinned submodules failed to initialize.' }
& git log -1 --format='%h %s'
~~~

If a prior branch contains custom files or configuration, **do not delete
it automatically**. The retained source branches and archived refs preserve
their Git history; settings and secrets may also exist outside Git.

## 2. Validate source, back up the old runtime, rebuild and verify

The updater first fast-forwards to the reviewed source, then runs
transactional build/install. The consolidator snapshots the **entire**
existing native runtime to a sibling
AeroHelm-GCS-release-backups folder before replacing the executable or
plugin. Its rollback path restores the previous runtime after build error.

~~~powershell
if (!(Test-Path -LiteralPath '.\bin\Release\net461\MissionPlanner.exe')) {
    throw 'Existing Windows runtime missing. Do not use the in-place updater; establish a clean build separately.'
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
if ($LASTEXITCODE -ne 0) { throw 'Windows source contract checks failed.' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Run-AeroHelmOfflineChecks.ps1
if ($LASTEXITCODE -ne 0) { throw 'Offline receiver/survey regressions failed.' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-AeroHelmLatest.ps1 -Apply
if ($LASTEXITCODE -ne 0) { throw 'Windows transactional update failed; do not launch.' }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Verify
if ($LASTEXITCODE -ne 0) { throw 'EXE/plugin hashes do not match the exact source manifest.' }
~~~

The currently installed native application is still named MissionPlanner.exe
and plugin AeroHelm.Cloud.Plugin.Validation.dll for compatibility. Its
operator-facing title is Vihanga GCS.

### When release verification reports a stale manifest

Do not manually change `build-logs/aerohelm-consolidated-release.json`,
copy old DLLs over the new runtime, delete the working backup or run
`git reset --hard`. The manifest records the **exact Git source commit**,
compiled `MissionPlanner.exe` hash and installed plugin DLL hash.
A source update after a prior build is expected to invalidate the old
manifest until that new source is compiled.

Fetch the latest fix and diagnose without modifying the installed runtime:

~~~powershell
git fetch origin feat/vihanga-phase3-native-toolbar
git pull --ff-only origin feat/vihanga-phase3-native-toolbar
if ($LASTEXITCODE -ne 0) { throw 'Could not fast-forward to reviewed source.' }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\Consolidate-AeroHelmWindows.ps1 -Stage Diagnose
if ($LASTEXITCODE -ne 0) { throw 'Read-only diagnostics could not run.' }
~~~

`-Stage Diagnose` prints separate recorded-vs-actual fields without
changing the manifest. To create a valid manifest for the **new commit**
you must successfully rebuild and reinstall with rollback protection,
with all Mission Planner windows closed:

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\Repair-AeroHelmWindows.ps1 -Apply
if ($LASTEXITCODE -ne 0) { throw 'Transactional repair failed; preserve runtime backup and build logs.' }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\scripts\\Consolidate-AeroHelmWindows.ps1 -Stage Verify
if ($LASTEXITCODE -ne 0) { throw 'Verification failed; do not clean or launch yet.' }
~~~

If the diagnostics show an EXE/DLL hash mismatch even when the commit
matches, inspect the corresponding binary, the `build-logs` build report,
duplicate plugins and installed assembly paths. **Do not rebaseline hashes
to bypass a mismatch.**

The cleanup script now keeps `Get-ChildItem ... -Force -ErrorAction Stop`
on one command line, so Windows PowerShell 5.1 does not attempt to
execute `-ErrorAction` as an independent command. Dry-run cleanup is safe
to retry only after updating the source; `-Apply` additionally requires
a verified runtime.

## 3. Clean obsolete generated files, not the last-good backup

The installer moves only **known** obsolete AeroHelm plugin DLLs to its
rollback archive and removes the duplicate interpreted AeroHelmCloud.cs
only after a successful verified DLL install. Unrecognized DLLs are
reported and left alone. Generated .NET/Python test outputs can be removed
after exact-build verification.

~~~powershell
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Cleanup-AeroHelmGenerated.ps1
if ($LASTEXITCODE -ne 0) { throw 'Cleanup inspection failed.' }
# Only when the displayed generated paths are correct:
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Cleanup-AeroHelmGenerated.ps1 -Apply
if ($LASTEXITCODE -ne 0) { throw 'Generated-output cleanup failed.' }

$backupRoot = Join-Path (Split-Path -Parent $repo) 'AeroHelm-GCS-release-backups'
if (Test-Path -LiteralPath $backupRoot) {
    Get-ChildItem -LiteralPath $backupRoot -Directory |
        Sort-Object LastWriteTime -Descending |
        Select-Object Name,LastWriteTime,FullName
}
~~~

Do not remove the most recent confirmed working runtime, settings XML,
logs, cloud secrets, or backup folders at this stage. Generated source maps
have already been removed from the reviewed GCS branch; local deleted
assets disappear when Git switches to the updated tree, but historic
Git object storage is **not** automatically purged.

## 4. With Pi/FC powered on: passive tests first

This inventory does **not** open COM or Pi UART and does not interrogate
the flight controller. Set optional values to your real private Pi host and
cloud origin, if known. An SSH port probe is not proof of Pi identity;
a cloud TCP probe is not proof of TLS, JWT or fresh MAVLink.

~~~powershell
# Optional; assign only values actually deployed in your environment:
# $env:VIHANGA_PI_HOST = 'your-pi-hostname-or-ip'
# $env:AEROHELM_CLOUD_URL = 'https://your-private-node.ts.net'

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-VihangaPassiveBench.ps1 -PiHost $env:VIHANGA_PI_HOST -CloudOrigin $env:AEROHELM_CLOUD_URL -Export
if ($LASTEXITCODE -ne 0) { throw 'Passive connectivity checks failed.' }

# The private M1 backend preflight uses an interactive operator login POST
# followed by read-only API GETs; no MAVLink/FC command is transmitted.
if ($env:AEROHELM_CLOUD_URL) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmPrivatePreflight.ps1 -CloudUrl $env:AEROHELM_CLOUD_URL -RequireConnectedPi -RequireCompleteParameters
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'Cloud/Pi M1 preflight did not meet its gates; preserve its report.'
    }
}
~~~

**M1 and cloud R2 are different API/identity contracts.** The M1 preflight
requires the hardware-telemetry-only M1 service; it will correctly fail
against an R2-only OIDC deployment. Do not bypass these checks or interpret
a successful TCP connection as fresh FC attitude/heartbeat.

## 5. Open Vihanga in no-auto-connect mode

~~~powershell
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 -NoAutoTcp
~~~

The GUI must show DATA / PLAN / SETUP / CONFIG, and CLOUD / PATHS /
SURVEY / VISION / LINK / OPS / INTEL / MORE. Start with CLOUD and LINK
inspection. PATHS selection is deliberate. **No automatic cloud-to-radio
takeover is implemented.** Do not initiate local COM/radio control until
the propeller-free bench plan, vehicle ID, operator authority, autopilot
failsafes and connection ownership are confirmed.

For a separately authorized local backup test (propellers removed, disarmed):

~~~powershell
# Close the current Mission Planner instance normally first.
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-DirectBackup.ps1 -AcknowledgePilotAuthority
~~~

This intentionally enters the native local pilot link, which may carry
real control traffic. It is not part of passive network verification.

## 6. Capture certification readiness without claiming clearance

~~~powershell
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-VihangaCertification.ps1 -RunSourceTests -CheckGitHubCI -Export
~~~

The assessment may legitimately report **NOT CLEARED** until exact-head
GitHub Actions, Windows GUI, cloud identity and physical failsafe/bench
acceptance exist. It always leaves physical flight certification false.

See VIHANGA_CERTIFICATION.md and VIHANGA_PHASE3_HANDOVER.md for the formal
evidence and independent acceptance requirements. Never run these
commands with an armed aircraft as an informal smoke test.
