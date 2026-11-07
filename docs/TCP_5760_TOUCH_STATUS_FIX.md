# AeroHelm — cloud and TCP status separation (Windows GCS)

## Problem from 2026-11-04 operator screenshot

HOME showed a large, twice-repeated status:
`LOCAL TCP / PORT 5760 IN USE ... frames 0 ... HB 0 ... ATT 0`.
Cloud health simultaneously reported fresh Pi telemetry but an older Oracle
backend with no real heartbeat/attitude truth counters. The screen was
clipped and it looked like cloud telemetry had completely failed.

These are **two independent data paths**:

1. Pi → Oracle reports a fresh stream of some MAVLink packets.
2. Local Windows → 127.0.0.1:5760 requires an exclusive loopback listener.
   If a previous Python adapter owns that port, the new integrated native
   receiver cannot listen. Oracle's freshness does **not** prove that a
   Windows GCS viewer has attached, nor prove real ATTITUDE or HEARTBEAT.

## Fix in source

- One compact LOCAL TCP headline at the top of HOME; verbose real frame
  counts and roll/pitch remain available via tooltip and LINK diagnostics.
- One separate CLOUD headline at the bottom, with no appended TCP summary;
  avoids duplicate/clipped text and clearly labels older Oracle releases.
- A native receiver waiting for a local viewer says **READY — CLICK FLIGHT**
  rather than incorrectly implying Pixhawk heartbeat is missing.
- Native port bind failures say **PORT 5760 OCCUPIED — CHECK LISTENER**.
  Neither the GCS nor the launcher assumes the owner is Python.
- The direct touch launcher now checks port 5760 **before** spawning the GCS
  and reports the owning PID; only when the command line explicitly matches
  `aerohelm_tcp_bridge.py` does it offer the `-UseLegacyPythonBridge` option.
  It never terminates another process or opens a public TCP listener.
- Offline source and Windows page contract tests guard separation and the
  safe preflight check.

## Safe user-side repair

Close Mission Planner first; identify the listener without disclosing any
process arguments or secrets:

```powershell
Get-NetTCPConnection -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue |
  ForEach-Object {
    $p=Get-CimInstance Win32_Process -Filter "ProcessId = $($_.OwningProcess)"
    [pscustomobject]@{
      PID=$p.ProcessId
      Process=$p.Name
      OldAeroHelmPythonBridge=($p.CommandLine -match 'aerohelm_tcp_bridge[.]py')
    }
  }
```

If it is the previously started AeroHelm Python bridge, stop it in **its
own** PowerShell terminal with `Ctrl+C`. Don't terminate an unidentified
process. Alternatively, run `Start-AeroHelm-Session.ps1 -UseLegacyPythonBridge`
to explicitly use a verified existing legacy bridge, with the native bridge
disabled. That fallback has different login behavior.

Update the reviewed branch and rebuild the native plugin (the consolidated
launcher verifies exact source/EXE/plugin SHA-256):

```powershell
cd C:\Users\saita\AeroHelm-GCS
git status --short
git pull --ff-only origin feat/gcs-research-cybershield-twin-ui
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Repair-AeroHelmWindows.ps1 -Apply
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Session.ps1
```

All changes are in draft source until the Windows build and UI acceptance
succeed. No Pi, Oracle service or Pixhawk parameters are changed.

## Honest cloud-data limitation

If the Cloud line still reads **LEGACY SERVER**, the running Oracle
deployment doesn't yet expose the newer real HEARTBEAT/ATTITUDE health
fields. UI changes alone cannot make those fields real. Oracle M1 source
updates are separate from deployment; do not infer live HUD readiness
from `fresh=true` or cumulative `frames_received`.
