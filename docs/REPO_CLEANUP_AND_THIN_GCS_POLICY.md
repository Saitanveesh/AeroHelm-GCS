# AeroHelm — repository cleanup and thin-GCS boundaries

**Source of truth:** `Saitanveesh/AeroHelm-GCS`.
**Branch:** `chore/lean-gcs-source-artifact-cleanup` stacked on `master`.
**Intent:** remove verified generated debug artifacts, prevent secret/model
weight reintroduction, and retain the upstream Mission Planner build.

## Actual cleanup

Removed **94 generated Cesium/Bootstrap JS/CSS source-map files** under
`ExtLibs/wasm/wwwroot` (approximately **23.55 MB** of tracked working-tree
bytes). They are developer debugging metadata, not executable JavaScript
or CSS. The corresponding minified/compiled runtime assets are **retained**.

No `ExtLibs/**/*.dll`, SDK source, ArduPilot firmware binaries, local backup
telemetry paths, flight log test fixtures, `MissionPlanner.sln`, native
`GCSViews`, original tracked dependency sources or full Git history were
deleted. The branch removes these source files from the current tree; **it
does not purge them from existing Git history or claim that an existing clone
will shrink by 23.55 MB**.

The root `.gitignore` now preserves authored docs, excludes cloud local
environments and cache output, and ignores experimental pretrained model
weights. The read-only `ci/check_repository_hygiene.py` gate detects
reintroduction of source maps, tracked cloud secrets/model experiments, and
accidental removal of core GCS files.

## Workload allocation

| Workload | Approved owner | Required behavior |
| --- | --- | --- |
| Real flight control, RC backup, direct telemetry radio | Pixhawk and local authorized pilot | Local; must not depend on Oracle |
| Flight HUD essentials and connection selection | Thin native GCS | Render real fresh telemetry; stale when disconnected |
| Multi-UAV assignment and mission work ledger | AeroHelm Cloud | Durable cloud-side computation, private API |
| Fleet analytics, battery/GNSS anomaly assessment, historical telemetry | AeroHelm Cloud | Query precomputed summaries; never require local heavy replay |
| Video transcoding, GPU inferencing, terrain/weather planning | Cloud worker (future) | Async bounded work with timeouts and explicit data provenance |
| Log export, compressed maps, ML model experimentation | Cloud storage / isolated worker | Do not bundle test artifacts or AI weights with GCS |

The present GCS remains an upstream Mission Planner fork; **this source
cleanup does not magically reduce its Windows runtime memory or CPU load.**
A future separately reviewed GCS release build may use lazy-loaded cloud
panels, optional plugins, and a minimal packaging manifest. Do not remove
core GCS assemblies or direct telemetry functions to chase size alone.

## Candidate assets NOT removed

- `ExtLibs/Xamarin/**/*.aar` and ArduPilot `.so` firmware resources:
  substantial in size but may be used by separate mobile/HIL projects.
- `ExtLibs/*.dll`, `Drivers/`, `.nuget/NuGet.exe`:
  legacy Windows build and installer dependencies; require a clean
  native Windows rebuild before removal.
- `Lib.zip`, `ParameterMetaDataBackup.xml`, `airports.csv`:
  possible runtime/data fallback dependencies; verify consumers first.
- Existing native Mission Planner tests and flight acceptance fixtures:
  keep for regression and safety despite their age.
- Historical Git branches, tags, PR release manifests: no deletions until
  their release/rollback ownership is explicitly documented.

## Merge acceptance

1. Verify **exact-head** repository hygiene CI and run
   `python ci/check_repository_hygiene.py` on a clean checkout.
2. Windows native MSBuild smoke and a standalone WASM/Cesium static-load
   check must pass before the 94 source-map deletions reach `master`.
3. Record previous accepted Windows binaries and SHA256 manifest; roll back
   by reverting this cleanup commit if any expected runtime asset is missing.
4. Keep the cloud R1 PR separate. Its Docker image already excludes the
   Mission Planner tree via `cloud/Dockerfile.dockerignore`.
5. Validate GCS resource usage (cold startup, idle CPU/RAM, CPU under
   telemetry and high-rate updates) before claiming runtime savings.

**No Oracle, Raspberry Pi, Windows installation, telemetry-radio, or
aircraft was modified by these Git commits.**
