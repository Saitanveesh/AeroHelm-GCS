# AeroHelm GCS — safe Windows refresh and generated-output cleanup

**Source:** `Saitanveesh/AeroHelm-GCS`
**Current development branch:** `feat/gcs-touch-ux-readonly-fleet-r1`.
The branch is a draft, not yet a reviewed/accepted production build.

The existing updater was pinned to `feat/gcs-research-cybershield-twin-ui`.
This branch updates **Update**, **Consolidate**, **Build-Integrated**, and
**Start-Touch** so the latest UI branch can be built only after a verified
full-runtime backup, an exact Git revision and local EXE/DLL hashes.
It does **not** auto-delete unrecognized plugins or old recovery backups.

## Preflight — run in a fresh Windows PowerShell

Close Mission Planner and the old telemetry bridge normally. Do not terminate
unidentified TCP listeners. If local source changes exist, review, commit or
back them up yourself before switching branches. Never use `git reset --hard`
or `git clean -fdx` to clear the warning.

```powershell
$ErrorActionPreference = 'Stop'
Set-Location 'C:\Users\saita\AeroHelm-GCS'
if (Get-Process MissionPlanner -ErrorAction SilentlyContinue) {
    throw 'Close Mission Planner before updating.'
}
git status --short
if (@(git status --porcelain).Count -gt 0) {
    throw 'Local source/untracked files exist. Preserve/review them first.'
}
git fetch --prune origin
if ($LASTEXITCODE -ne 0) { throw 'Git fetch failed' }
$branch='feat/gcs-touch-ux-readonly-fleet-r1'
git show-ref --verify --quiet "refs/heads/$branch"
if ($LASTEXITCODE -eq 0) {
    git switch $branch
} else {
    git switch --track -c $branch "origin/$branch"
}
if ($LASTEXITCODE -ne 0) { throw 'Branch switch failed' }
git merge --ff-only "origin/$branch"
if ($LASTEXITCODE -ne 0) { throw 'Review diverged local branch before proceeding' }
git log -1 --oneline
```

## Read-only validation

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
if ($LASTEXITCODE -ne 0) { throw 'Launcher syntax/source test FAILED' }

python -m unittest -v Tools.test_aerohelm_offline_gcs Tools.test_aerohelm_touch_ux Tools.test_aerohelm_build_globs
if ($LASTEXITCODE -ne 0) { throw 'GCS offline checks FAILED' }

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-AeroHelmLatest.ps1
if ($LASTEXITCODE -ne 0) { throw 'Release inspection FAILED' }
```

## Full runtime backup + update + native tests

A previous `bin/Release/net461/MissionPlanner.exe` must exist for the
transactional upgrader to snapshot and restore it. If missing, perform a
separately reviewed initial solution build; **do not fake an old runtime**.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-AeroHelmLatest.ps1 -Apply
if ($LASTEXITCODE -ne 0) { throw 'GCS build or verified install FAILED' }

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Verify
if ($LASTEXITCODE -ne 0) { throw 'Compiled EXE/DLL hashes FAILED' }

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Run-AeroHelmOfflineChecks.ps1
if ($LASTEXITCODE -ne 0) { throw 'Hardware-free MAVLink/survey tests FAILED' }
```

The updater makes a **full previous-runtime snapshot** outside the source
tree at `..\AeroHelm-GCS-release-backups\<timestamp>\runtime\`. It builds
with native MSBuild and archives only known obsolete AeroHelm plugin DLLs.
The release manifest under `build-logs/aerohelm-consolidated-release.json`
contains the actual Git commit, EXE and plugin SHA-256 checksums. The old
runtime is restored automatically if release build/installation fails.

## Deep clean: preview first, then remove only generated files

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Cleanup-AeroHelmGenerated.ps1
if ($LASTEXITCODE -ne 0) { throw 'Cleanup inspection FAILED' }
# Inspect the printed paths and sizes before choosing to apply.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Cleanup-AeroHelmGenerated.ps1 -Apply
if ($LASTEXITCODE -ne 0) { throw 'Generated-artifact cleanup FAILED' }
```

Allowed targets are ONLY build/test-output directories:
`Plugins/AeroHelmCloud/{bin,obj}`, the native bridge and survey test
`{bin,obj}`, and known temporary Python cache directories.
Any tracked file, symlink/junction, dirty checkout, running Mission Planner,
wrong branch or unverified release blocks cleanup. Files under the active
`bin/Release/net461`, source code, rollback backups, mission/telemetry logs,
`cloud/secrets`, configuration and Git history remain untouched.

`git clean -ndX` may be used to **preview** ignored files but do NOT blindly
run `git clean -fdx` or delete `build-logs` or release backup directories.
The safe cleaner is intentionally narrower than `git clean`.

## UI smoke without Oracle or an aircraft

After all checks pass, launch native Mission Planner **without a cloud
bridge**, and manually click TOUCH in the native toolbar:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Touch.ps1 -ManualTouch -LocalDirectBackup -NoInternalBridge -NoAutoTcp
```

This uses the offline local-maintenance entry, not an automatic COM/pilot
radio connection. Exercise F1–F10, Esc, touch target layout, Fleet/Ana
"unavailable" states, focus loss/reconnect, native Flight Data, and settings.
Before operational use, test screen scaling at 100/150/200%, 1024×768
and 1920×1080; compare idle GCS CPU/RAM and actual rendered HUD values.

**No command in this runbook sends flight controls, changes aircraft
parameters, updates Oracle/Pi or authorizes an aircraft flight.**
