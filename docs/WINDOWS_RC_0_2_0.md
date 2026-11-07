# AeroHelm Windows GCS 0.2.0 RC — one-session test plan

**Scope:** Source release candidate for Windows Mission Planner. iOS/Android are
deferred. No physical flight-command authority is added. Stable `master`
and Pi/Oracle deployments remain unchanged at this stage.

## What is combined

The single GCS development trunk `feat/aerohelm-touch-console` inherits
the older read-only cloud/M2 transport, native Mission Planner plugin,
survey generator, simulated OPS, supervised maintenance, security, fleet,
analytics, LINK, VISION, SETTINGS and the exact 3×3 monochrome dashboard.

The release-candidate development branch adds transactional plugin install,
hash-checked automatic error recovery and explicit human rollback, safe
startup preflight, non-hidden native HELP/Simulation menus, and CI-only
install/rollback roundtrip checks.

## Release gates (software and hardware are separate)

| Gate | Objective | Execution status |
| --- | --- | --- |
| S1 | Native source-contract checks for all nine Windows modules and cloud-only GET pages | Script added; run required |
| S2 | Full Visual Studio/MSBuild Windows Mission Planner Release solution | Automated job added; run required |
| S3 | Compiled plugin load and no duplicate runtime source plugin | Installer/launcher checks added; runtime verification required |
| S4 | Disposable CI rollback test with prior DLL and old-source sentinels | Automated job added; CI run required |
| S5 | Nine-tile HOME UX, 125/150% DPI, keyboard/touch/ESC and native FLIGHT/PLAN | Physical Windows GUI required |
| S6 | Login, expiry, stale/unavailable telemetry, cloud health, private cameras, historic data | Local cloud test required |
| S7 | M2 verified complete parameter loading, no outbound control, Pi/Pixhawk disarmed and props removed | Hardware bench required |
| S8 | Signed Windows distribution, update/rollback, field safety | Not ready; independent release track |

No compilation or static check authorizes S5-S8. The readiness inspector
always marks user-interface and Pi acceptance false by default.

## Commands for isolated Windows development checkout

With working Mission Planner and all aircraft power OFF, back up the
previously working installation before building:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git fetch origin
git switch feat/aerohelm-touch-console
git pull --ff-only
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Inspect-AeroHelmWindowsStage.ps1 -Stage Source
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Inspect-AeroHelmWindowsStage.ps1 -Stage Build -SkipBranding
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Touch.ps1 -ManualTouch -ReadOnlyCloudHUD
```

Stage Build modifies ONLY local checkout build output, not a signed production
installer. Launcher refuses if compiled plugin is absent, invalid-sized,
or the duplicate old C# source is still present. It forces
AEROHELM_CLOUD_READONLY=1. The native HELP and Simulation menus remain visible
unless an explicit compact-toolbar environment flag is set.

Check local backup paths printed after installation. The installer saves a
timestamped backup of the prior DLL and optional legacy .cs plugin under
git-ignored `build-logs/plugin-backups`; records and verifies SHA-256;
installs new DLL only after staging/verifying it; removes duplicate legacy
source only after a secure backup; and attempts to restore the prior files
on installation errors. If rollback fails, keep Mission Planner stopped.

To inspect a backup without changing the executable:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Restore-AeroHelmPlugin.ps1 -BackupName YYYYMMDD-HHMMSS-XXXXXXXX
```

Only when the operator explicitly requests rollback and Mission Planner
is closed:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Restore-AeroHelmPlugin.ps1 -BackupName YYYYMMDD-HHMMSS-XXXXXXXX -Apply
```

The special Test-AeroHelmPluginRoundtrip.ps1 **must never be used locally**:
it seeds intentionally invalid old DLL bytes to test installation/rollback
and refuses to run without GitHub Actions + disposable CI environment flags.
It is for ephemeral CI workspaces only.

## Hardware and cloud validation

Use the cross-repository `docs/RELEASE_TRAIN_WINDOWS_PI.md` and the
AeroHelm backend M2 manual. For Pi/Pixhawk use existing Tailscale private
connectivity, disarmed FC and all propellers removed, and test no GCS-to-Pi
command forwarding. Do not introduce physical autonomous missions, motor
tests, parameter writes, arbitrary MAVLink passthrough or unsupervised
calibration during this RC acceptance.

## Remaining non-code prerequisites

- A Windows Visual Studio full-framework MSBuild runner capable of
  compiling MissionPlanner.sln and the AeroHelm plugin
- Access to test the actual Windows GUI, plugin loader and connected
  hardware with a physically present operator
- Actual working camera endpoints for each claimed Pi video source
- Production signing identity and separate authenticated deployment approvals

## Ship/no-ship decision

**SHIP = NO** until native Windows compilation, disposable rollback, human
GUI checks and propeller-off Pi/Pixhawk bench tests pass. Cloud
authentication and cached parameters alone are never evidence of
flightworthiness.
