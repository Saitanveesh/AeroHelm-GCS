# VIHANGA Ground Station — Windows deployment and recovery

This procedure applies to `Saitanveesh/AeroHelm-GCS` branch
`feat/vihanga-phase3-native-toolbar`. The current source retains the legacy
AeroHelm executable, DLL and environment variable identifiers for compatibility.
The application title is **Vihanga GCS**. All commands below are Windows
PowerShell commands, run from the checked-out repository root.

## Prerequisites and safety

Use Windows with Visual Studio/MSBuild and the .NET build toolchain described
in `docs/UPSTREAM_BUILD_REFERENCE.md`. Close Mission Planner and preserve
the last working installation, settings and logs. Confirm that `git status`
has no uncommitted changes. Do not run `git clean` or force-reset the branch.
The release scripts reject a dirty checkout and verify the source commit and
SHA-256 of the compiled EXE and plugin DLL.

```powershell
git fetch origin feat/vihanga-phase3-native-toolbar
git switch --track origin/feat/vihanga-phase3-native-toolbar
git status --short
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
python -m unittest -v Tools.test_aerohelm_offline_gcs Tools.test_vihanga_native_toolbar Tools.test_aerohelm_build_globs
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Audit-AeroHelmModules.ps1
```

If the branch is already checked out, use `git pull --ff-only`, never a
forced update. If the branch contains work of your own, preserve it first.

## Transactional build and verification

Inspection and cleanup are non-destructive by default. The build stage
backs up the existing native runtime; on failure it attempts restoration.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-AeroHelmLatest.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Build -Apply -WithBranding
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Verify
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Run-AeroHelmOfflineChecks.ps1
```

The default desktop entry point is `scripts/Start-VihangaGCS.ps1`.
The old `Start-AeroHelm-Touch.ps1` remains only as a compatibility alias,
not a touch dashboard. Cloud auth is required except for an explicit
local-maintenance session.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1
```

For a physically present pilot using an independent USB/telemetry radio,
close the current Mission Planner instance before starting a local backup.
The backup launcher requires explicit pilot authorization:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-DirectBackup.ps1 -AcknowledgePilotAuthority
```

Do not connect a real vehicle during automated software checks. Do not
assume a working camera, cloud login, a valid JWT, fresh MAVLink heartbeat or
an accessible Tailscale endpoint until verified on the target workstation.

## Toolbar and acceptance

Native **DATA / PLAN / SETUP / CONFIG** are kept. The custom toolbar is
**CLOUD / PATHS / SURVEY / VISION / LINK / OPS / INTEL / MORE**.
MORE contains **FLEET, SECURITY, ANALYTICS, PARAMETERS, SETTINGS**.
Flight Data, Flight Plan and local radio are native Mission Planner
functions. No automatic cloud-to-radio handover is implemented.

Windows acceptance still requires an exact-commit build, launch and DLL
loading smoke test, link-state fail-closed checks, UI scaling,
correct cloud API responses, camera privacy checks, Pi / autopilot bench
evidence and an authorized flight-test program before aircraft use.

## Generated-output cleanup

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Cleanup-AeroHelmGenerated.ps1
# Apply only after reviewing the printed candidate paths:
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Cleanup-AeroHelmGenerated.ps1 -Apply
```

Cleanup excludes source, logs, operator configuration, backups and the
installed native EXE/DLL. Preserve those for rollback.
