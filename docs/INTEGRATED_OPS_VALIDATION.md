# AeroHelm integrated test release candidate — operator checklist

**Code branches are still draft PRs**, not deployments:
- GCS: `feat/aerohelm-workspace-vision-mission` (PR #3)
- Backend/Pi: `feat/ops-mission-calibration-security` (PR #12)
- Legacy verified telemetry foundation remains on its original branches.

## Test modes must stay separated

| Mode | Infrastructure | Allowed | Not allowed |
| --- | --- | --- | --- |
| Desktop offline | Only Windows GCS | Startup/local login, SURVEY draft, VISION launcher, LINK diagnostics | Real aircraft mission commands |
| Simulator | Windows loopback API (simulation enabled) | AUTONOMY draft, start/pause/resume/abort, lease expiry | Real Pixhawk commands |
| Live view | Pi + Oracle existing M1 + Windows TCP | Telemetry HUD + LINK metrics | GCS-originated hardware commands |
| Maintenance | Pi + Oracle revised API + GCS | Locally approved gyro/barometer/simple accel calibration (disarmed, props removed) | Arming/motor tests, full accel/compass wizard, param writes |
| Flight actuation | NOT AVAILABLE | Nothing | Remote arming, AUTO, takeoff, waypoint execution, unattended UAV |

## A. One Windows build, no aircraft power needed

In `C:\Users\saita\AeroHelm-GCS`:

```powershell
git fetch origin
git switch --track origin/feat/aerohelm-workspace-vision-mission
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Integrated-AeroHelm.ps1
```

If branch already exists, switch it normally and `git pull --ff-only` first.
The script runs offline survey tests, the Visual Studio full MSBuild,
reinstalls the plugin and applies AeroHelm title branding.
It **does not** control the Pixhawk.

To enter offline diagnostics without a cloud connection, use
`MissionPlanner.exe --local-maintenance`. This is a recovery path,
not cloud login and not flight permission.

Expected buttons: CLOUD, SURVEY, VISION, LINK, OPS.
Startup cloud sign-in appears when branded or when
`AEROHELM_REQUIRE_LOGIN=1`.

**Known limitation:** The GCS branch has not yet passed an actual Windows
MSBuild run on the user machine. Fix any compiler warnings/errors first;
do not copy half-built DLLs into the working instance.

## B. Isolated simulation test — no Pi, no Oracle required

Backend on Windows:

```powershell
cd C:\Users\saita\AeroHelm
git fetch origin
git switch --track origin/feat/ops-mission-calibration-security
powershell -ExecutionPolicy Bypass -File .\scripts\Start-Local-AeroHelm-Sim.ps1
```

This reads the existing private, locally retained `.env`, starts the API
**only on 127.0.0.1:8001**, and enables the software mission simulator.
It never opens a Pixhawk serial port.

From a second Windows PowerShell, launch the branded GCS with:
```powershell
$env:AEROHELM_CLOUD_URL="http://127.0.0.1:8001"
cd C:\Users\saita\AeroHelm-GCS
Start-Process .\bin\Release\net461\MissionPlanner.exe
```

Sign in using the existing local operator credentials, open OPS and
provide 2-80 short waypoints **near the simulator's fictional starting
position** (approx 12.9716, 77.5946); create the draft, acquire a
30-second renewable simulation lease, start/pause/resume/abort, and refresh
progress. Test cancellation and token expiry. This is an AeroHelm software
simulator, NOT evidence from ArduPilot SITL.

## C. Existing native cloud telemetry M1

After powering Pi + Pixhawk and allowing the existing agents to start:
- Pi connects to Oracle HTTPS over Tailscale; API
  `/api/v1/mavlink/health` has fresh=true and increasing frames.
- Start the read-only Python Windows TCP adapter (previously fixed) and
  Mission Planner TCP 127.0.0.1:5760 with
  `AEROHELM_CLOUD_READONLY=1`.
- Verify native HUD attitude and mode while props are removed.
- For M1, downloading parameters/config/mission or issuing any flight
  commands is unavailable; the bridge drops GCS-originated MAVLink.

Do not alter Pixhawk TELEM1 wiring or TELEM2 radio configuration from
the previously verified full-duplex bench setup.

## D. Supervised physical calibration (separate signoff)

This requires a **new** backend deployment and Pi agent checkout of the
experimental PR branch, only after review and backup. Do NOT apply this
to a flight-ready UAV. Remove ALL propellers, disarm, physically supervise.

- On Oracle, deploy reviewed backend once; keep private Tailscale Serve,
  one uvicorn worker, and the same existing .env secrets.
- On the Pi, load the reviewed agent, keep a single UART owner, and
  set `AEROHELM_SUPERVISED_CALIBRATION_ENABLED=true` only during
  the supervised bench interval.
- In GCS CLOUD/OPS, select GYRO, BAROMETER or ACCEL_SIMPLE, acknowledge
  physical conditions and request a one-shot job.
- Within 45 seconds, on the Pi's actual terminal, approve that exact
  request ID with
  `agent/.venv/bin/python -m agent.supervised_calibration UUID`.
- Poll the OPS calibration status. Report accepted/rejected/timeout
  based on actual FC COMMAND_ACK; accepted does not confirm calibration
  accuracy or subsequent flight readiness.
- After the test, turn the Pi flag back OFF.

No support for full six-position accelerometer, compass wizard,
RC endpoint calibration, ESC calibration, firmware flashing or armed
flight control via the cloud at this milestone.

## E. Security and latency evaluation

Run on Windows using the new backend branch:
```powershell
python scripts\measure_cloud_latency.py --url https://aerohelm-vcn.tailf3ab6a.ts.net --samples 40
```

This reports pooled HTTPS p50/p95/jitter, not motor command latency.
Run `tailscale ping aerohelm-vcn` to determine direct versus DERP.
Open LINK in GCS for heartbeats, dropped-frame counts and rate.
Test unplug/replug, stale sessions, rejected commands, expired lease and
cloud restart, always with disarmed propeller-free hardware.

## No automatic physical flight tests

Do not treat login success, cloud telemetry, valid waypoints or a
COMMAND_ACK as authorization for mission execution. Physical autonomous
flight requires separate hardware geofence, GPS/EKF and battery acceptance,
RC handover, FC GCS failsafe, MAVLink signature decisions, independent
Pi-local authorization and SITL-to-bench-to-field testing.
