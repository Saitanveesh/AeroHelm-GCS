# AeroHelm GCS — release consolidation and lock record

**Date:** 2026-11-04  
**Source branch:** `feat/gcs-research-cybershield-twin-ui` (includes the stacked unified-login PR #20 and research UI PR #21).  
**Release label:** `GCS integration candidate — hardware restricted`.  
**Production declaration:** **NOT yet qualified.** No native Windows CI run has been observed for this GCS branch, and no end-to-end HUD test has passed.

This milestone is a **feature freeze**, not a certification. Additional speculative plugins should remain out of scope until the exact version is built and physically accepted.

## Actual source/module audit

| Feature | Source wiring | Native Windows runtime proof | Cloud/backend caveat |
|---|---|---|---|
| FLIGHT | Mission Planner Flight Data + one-click loopback TCP | **Pending** | Requires genuine FC HEARTBEAT and ATTITUDE |
| PLAN | Mission Planner Flight Planner | **Pending** | Draft/local planning; no authorized cloud mission upload |
| VISION | Private camera viewer and camera URL settings | **Pending** | Pi IMX708 stream observed earlier, live Windows integration not requalified |
| OPS | Native operations/bench/simulation panels | **Pending** | Hardware autonomous actuation not authorized |
| SECURITY | Authenticated posture, CyberShield/evidence views | **Pending** | Full M2/V3 backend not deployed; Kerberos is a design only |
| FLEET | Vehicle registry and advisory scheduler lab | **Pending** | Scheduler cannot dispatch physical missions |
| LINK | Cloud freshness and live MAVLink diagnostics | **Pending** | Old deployed Oracle M1 does not expose the newer HB/ATTITUDE truth counters |
| ANALYTICS | History, digital shadow, evidence/twin lab views | **Pending** | Digital shadow is not validated physics digital twin; V3 opt-in |
| SETTINGS | Private URL/session settings and native MP navigation | **Pending** | Operator JWT and private Tailscale access must be smoke-tested |

Seven custom toolbar entry points are present in source: CLOUD, SURVEY, VISION, LINK, OPS, INTEL, TOUCH. Their native vector icons are implemented; upstream HELP/SIMULATION buttons are hidden in the operator profile but retained in the native maintenance executable.

The single compiled project explicitly includes ten AeroHelm C# source modules, with no dynamic plugin duplication by design. The installer backs up both the previously installed DLL and legacy interpreted `AeroHelmCloud.cs` before replacing anything.

## Why previous corrections did not appear

A `git pull` updates **source files**, not the existing
`bin\Release\net461\MissionPlanner.exe` or compiled plugin DLL.
The reported old LINK screen with missing heartbeat truth fields is consistent
with running the old compiled plugin and an older deployed Oracle backend.
New source has no effect until **the correct binary is rebuilt and installed**.

GCS plugin SETTINGS now reports the **actual loaded DLL SHA-256 prefix** and
module build ID, and the TOUCH window title displays the loaded plugin build
ID. Do not trust a title label alone; match the DLL hash against the release
manifest generated locally by consolidation.

## Operator-controlled Windows migration

**Do NOT run these commands on the Raspberry Pi or the Oracle server.**
Run on the Windows laptop with local access. Close Mission Planner first.
There is no destructive git reset/clean, no blind removal of old files and
no automatic server or flight-controller deployment.

PowerShell, in `C:\Users\saita\AeroHelm-GCS`:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git status --short
git fetch origin
$branch = 'feat/gcs-research-cybershield-twin-ui'
if (git branch --list $branch) {
    git switch $branch
} else {
    git switch --track -c $branch "origin/$branch"
}
git pull --ff-only origin $branch
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Inspect
```

Stop if Git reports conflicts, uncommitted tracked changes, or the audit
reports missing modules. **Do not use `git clean -fdx`,
`git reset --hard` or `Remove-Item -Recurse`** to resolve those.

After successful source inspection, with enough Windows disk space for a
**complete backup of the prior runtime**, execute:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Build -Apply
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Verify
```

The consolidation script verifies a clean tracked Git branch, enumerates the
nine panels and seven toolbar modules, backs up the entire prior runtime,
runs source and .NET/MAVLink tests, builds the full Mission Planner solution
and AeroHelm plugin using Visual Studio MSBuild, transactionally installs the
compiled plugin, archives only **known** obsolete DLL filenames after success,
and checks the resulting EXE and DLL against a local SHA-256 manifest.

On build or post-build failure, it attempts to restore the previous runtime
from the verified snapshot. Unknown or unrelated plugins are never silently
deleted. Existing flight controller/Pi/Oracle services are not changed.

The release manifest is at:
`build-logs\aerohelm-consolidated-release.json`.

Only after **Verify** passes should the operator launch:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Session.ps1
```

The new internal bridge occupies loopback port **127.0.0.1:5760**.
Stop any separately launched old Python TCP bridge beforehand. The launch
script refuses a conflicting listener instead of stealing its port.

## Mandatory release gates before saying "fully ready"

1. **Native Windows Build:** MSBuild returns zero, compiled plugin is installed, source contracts and actual C# bridge regression tests pass.
2. **Binary Provenance:** `-Stage Verify` matches full executable and installed plugin SHA-256; SETTINGS displays the same loaded DLL prefix.
3. **UI Smoke:** All nine workspaces and seven toolbar entry points open without exceptions; HELP/SIMULATION absent from operator toolbar; no duplicate CLOUD items.
4. **Live Data:** Real HEARTBEAT (ID 0), ATTITUDE (ID 30) and changing roll/pitch reach the native HUD; stale disconnected status must appear after link failure.
5. **Cloud Backends:** M1 can continue running, but M2 and opt-in V3 must be deployed and validated separately before CyberShield/digital shadow/twin/fleet research screens are called operational.
6. **Safety:** All propellers removed, vehicle disarmed and operator present before hardware acceptance. No cloud motor activation, remote arming, calibration, mission upload or parameter writes permitted.

**Outstanding:** These gates have **not** been independently passed for the user's actual Windows hardware and deployed cloud. The code audit confirms source integration, not guaranteed behavior.
