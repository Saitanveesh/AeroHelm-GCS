# AeroHelm GCS — November 4, 2026 duplicate-source build repair

## What actually failed

The Windows MSBuild log contained many `CS0101` (type already defined)
and `CS0111` (method already defined) errors. Every excerpted error referred
to C# source in a snapshot under
`AeroHelm-GCS\build-logs\release-backups\...\runtime\Plugins\`.

These do NOT demonstrate that every AeroHelm plugin is individually broken.
The main `MissionPlanner.csproj` uses SDK-style recursive source discovery.
The previous consolidation script put archived Mission Planner runtime files
back **inside** the source tree. Thus old runtime `Plugins\*.cs` classes
were compiled a second time from the backup.

## Implemented corrections

- `MissionPlanner.csproj` explicitly excludes `build-logs\**` in
  `Compile`, `EmbeddedResource`, and `None`. Historical snapshot files
  may remain safely in place.
- The consolidated release script saves **new** complete runtime backups
  to the sibling directory `C:\Users\saita\AeroHelm-GCS-release-backups`
  instead of the SDK source tree.
- `Build-AeroHelmGCS.ps1` refuses to launch a full solution build if
  the exclusion is missing. `Test-AeroHelmWindowsPages.ps1` verifies
  the exclusions during fast source-contract checks.
- Python regression `Tools.test_aerohelm_build_globs` ensures all three
  item types remain excluded and future snapshot locations stay external.
- `scripts\Repair-AeroHelmWindows.ps1` is a non-destructive,
  inspection-only entry point by default. With explicit `-Apply` it runs
  inspection, full source tests, native MSBuild, plugin installation and
  hash verification, reporting a concise list of any remaining errors.

## Windows operator procedure

Close Mission Planner and stop any running MSBuild processes **normally**.
You do **not** need to delete old backups, plugins or logs.

From PowerShell inside `C:\Users\saita\AeroHelm-GCS`:

```powershell
git status --short
git pull --ff-only origin feat/gcs-research-cybershield-twin-ui
python -m unittest -v Tools.test_aerohelm_build_globs
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Repair-AeroHelmWindows.ps1 -Apply
```

Stop if the Git checkout is not the reviewed
`feat/gcs-research-cybershield-twin-ui` branch, or if Git shows changes
that you have not backed up. **Never** use `git clean -fdx`,
`git reset --hard`, or indiscriminate `Remove-Item -Recurse`.

A successful run should print:

```text
PASS: Windows nine-tile wiring and read-only page contract checks.
AeroHelm Survey Planner: PASS (19 assertions)
AeroHelm native bridge: PASS (13 assertions)
Build succeeded.
CONSOLIDATED BUILD COMPLETE
VERIFY PASSED
```

If the build encounters **different** errors after the duplicate-source fix,
the script retains the full error log under
`build-logs\aerohelm-solution.log`; review its first distinct diagnostics
instead of deleting files. A successful Windows native build still does NOT
prove physical flight capability or cloud actuation. Flight controller
testing requires a disarmed, propeller-free bench environment.

When both build and verification succeed, close any existing external Python
TCP bridge on port 5760 and start the integrated desktop with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Session.ps1
```

This correction changes neither running Pi/Oracle services nor Pixhawk
parameters. Historical runtime snapshots are retained, not destroyed.
