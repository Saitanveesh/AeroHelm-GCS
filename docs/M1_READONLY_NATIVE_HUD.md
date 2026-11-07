# AeroHelm M1 — receive-only cloud Flight Data connection

The baseline Mission Planner `doConnect()` always calls `getParamList()` after
link identification. With the M1 AeroHelm TCP bridge, outbound `PARAM_REQUEST_*`
messages are intentionally discarded. Mission Planner therefore hangs at
"Getting Params 1" despite already receiving HEARTBEAT, ATTITUDE and occasional
unsolicited PARAM_VALUE packets. This is **expected for a receive-only transport**,
not a Pixhawk wiring defect.

In this fork, `MainV2.Connect()` supports **an explicit read-only opt-in**:
`AEROHELM_CLOUD_READONLY=1` plus the native **TCP** connection choice.
It passes `getparams=false` into `doConnect`. Native MAVLink frame decoding,
Flight Data/HUD, stream logs, and the native disconnect path stay intact.
All other connection types and normal TCP default still fetch parameters.

## Windows one-time build

Close all Mission Planner windows. From PowerShell:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git pull --ff-only origin feat/aerohelm-cloud-plugin
powershell -ExecutionPolicy Bypass -File .\scripts\Build-AeroHelmGCS.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Install-AeroHelmCloud.ps1
```

The full fork requires **Visual Studio's MSBuild**, not `dotnet build`.

## Enable read-only M1 mode

In the same PowerShell window used to launch Mission Planner:

```powershell
$env:AEROHELM_CLOUD_READONLY = "1"
Start-Process ".\bin\Release\net461\MissionPlanner.exe"
```

Start the existing Windows AeroHelm TCP bridge in a **different** PowerShell
window. It must display `Read-only TCP MAVLink bridge ready on 127.0.0.1:5760`.

Inside Mission Planner, select **TCP** and connect to `127.0.0.1:5760`.
The `Getting Params 1` modal should NOT appear on this opt-in connection.
The HUD may update if Pi/FC are transmitting the required messages.

**M1 limitations:** Param/mission/config panes are *not ready* for use, and
commands/ARM/CALIBRATION are never forwarded by the Windows read-only bridge.
Absence of a new GPS position indoors is normal. A stale flight-data reading
is not a valid indication of flight safety. All propellers must be removed
for bench testing.

Close Mission Planner to disable the experiment; this environment variable
only affects launches from PowerShell processes that inherit it. In any new
session without the variable, normal parameters remain enabled.

## Acceptance

1. Cloud broker `/api/v1/mavlink/health` shows device online/fresh and
   increasing `frames_received`.
2. Windows bridge accepts TCP client and reports first/100 frames forwarded.
3. Native Mission Planner Flight Data displays real STABILIZE, disarmed,
   heading, attitude, battery and other measurements when physically available.
4. No `Getting Params 1` blocking dialog with opt-in M1 mode.
5. Normal COM radio / USB sessions are unchanged; no cloud actuators enabled.

Later M2 development will add a *narrowly constrained, authenticated* duplex
transport permitting parameter reads / explicit read-only diagnostics, with
per-vehicle source authorization and no arbitrary command passthrough.
