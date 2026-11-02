# AeroHelm Platform V2 — consolidated integration & acceptance

**Development-only.** This document describes code committed to draft PRs. It
does not authorize autonomous flight, motor tests, MAVLink command passthrough
or unsupervised calibration.

- GCS PR #4: `feat/aerohelm-platform-v2` based on the former operator workspace.
- Backend/Pi PR #13: `feat/aerohelm-platform-v2` based on supervised operations.
- The existing deployed Pi and Oracle systems are unchanged.

## Features available for combined desktop test

| Module | Implemented in source | Acceptance boundary |
| --- | --- | --- |
| CLOUD/startup login | Existing HTTPS operator login and memory-only session | Token expiry/re-login UI and remote main transport still need more work |
| Native HUD | Prior M1 receive-only TCP and read-only param-sync bypass | Test no COM/telemetry radio and live roll/pitch; GCS commands blocked |
| SURVEY | Offline serpentine planning, WPL-110 draft export | No actual mission upload |
| OPS | Simulation mission lifecycle, lease renewal, supervised calibration UI | No live mission execution; calibration bench only |
| VISION | Tailscale HTTPS viewer launcher and Pi config preflight | Embedded live video not deployed |
| LINK | Device freshness, frames/sec, heartbeat age, dropped frames | Reporting only |
| INTEL/FLEET | Actual current vehicle registry, risk flags | One real hardware identity supported on backend |
| INTEL/PREFLIGHT | Coordinates, optional user polygon, energy estimate API, stable route digest | No real terrain, weather or airspace checks; never flight-authorized |
| INTEL/PARAMETERS | Read-only configuration drift against uploaded FC parameter snapshot | No remote parameter write |
| INTEL/HISTORY | Bounded RAM telemetry, offline archive, GeoJSON replay and report | Not a certified flight recorder |
| INTEL/SECURITY | In-memory HMAC audit chain and security posture | No MFA, mTLS, enterprise Kerberos, persistent audit DB |
| Edge offline journal | SQLite RAM->disk telemetry sequence with idempotent cloud upload | Disabled by default on Pi; no command buffering/replay |

## Phase A — one Windows offline compilation & GUI test

1. Keep the Pixhawk and Pi OFF. Close all Mission Planner windows.
2. In Windows PowerShell:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git fetch origin
git switch --track origin/feat/aerohelm-platform-v2
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Integrated-AeroHelm.ps1
```

If branch already exists: `git switch feat/aerohelm-platform-v2` then
`git pull --ff-only`.

3. The script must succeed at the .NET offline survey tests, full Visual
Studio MSBuild, plugin install and title branding **before** GUI launch.
4. For offline GUI inspection, bypass only the *cloud login* through
`MissionPlanner.exe --local-maintenance`. This does not authorize commands.
5. Confirm toolbar CLOUD / SURVEY / VISION / LINK / OPS / INTEL.
6. In SURVEY generate and export a short *.waypoints draft.
7. In INTEL PREFLIGHT load it; attempt invalid and valid polygons; confirm
`flight_authorized=false`, hash appears and unknown external checks are
clearly marked. INTEL security/history requires a running API.

**Do not deploy if Windows build fails**. The GitHub Python CI result cannot
validate this local .NET Framework/WinForms application.

## Phase B — local autonomy and security API, no aircraft

1. In separate Windows PowerShell:

```powershell
cd C:\Users\saita\AeroHelm
git fetch origin
git switch --track origin/feat/aerohelm-platform-v2
powershell -ExecutionPolicy Bypass -File .\scripts\Start-Local-AeroHelm-Sim.ps1
```

2. Launch GCS from **another** PowerShell with
`$env:AEROHELM_CLOUD_URL="http://127.0.0.1:8001"`.
3. Login with the user's existing .env operator credentials; do not generate
new secrets. Open OPS, request simulated mission, acquire lease, start,
pause, resume and abort. Check expiry and unauthorized request rejection.
4. Open INTEL. Confirm fleet, parameter drift, mission integrity and
security posture. Real hardware mission start must return HTTP 403.
5. Confirm that `/api/v2/security/posture` says arbitrary command forwarding
and real aircraft mission execution are disabled.

## Phase C — actual telemetry and archived journal (propellers removed)

1. After desktop tests pass and reviewed code is deployed, use existing
Tailscale network. Do **not** open public HTTP/API ports.
2. Verify Pi/Oracle/GCS on the same private tailnet and new backend API is
running one worker; compare `/health` and authenticated v2 fleet.
3. Raspberry Pi agent: enable journal only for a propeller-off bench interval:

```bash
export AEROHELM_OFFLINE_JOURNAL_ENABLED=true
```

This requires the v2 Pi agent branch and its existing private credentials.
4. Disconnect Pi networking while leaving UART and telemetry active:
SQLite journal should retain fresh local samples (max 5000).
5. Reconnect tailnet: historical POST sync increases, journal ACK deletes
only acknowledged sequence, and existing live telemetry remains *fresh*
only if an actual new Pixhawk report arrived.
6. INTEL History should distinguish bounded live RAM samples from archived
historical samples. Reboot or stop recording to verify continuity.
7. Run cloud link benchmark from the backend branch (does not command flight):

```powershell
python scripts\measure_cloud_latency.py --url https://aerohelm-vcn.tailf3ab6a.ts.net --samples 40
```

This only measures HTTPS request latency. Do not treat it as flight command
actuation latency.

## Phase D — supervised cloud calibration, separate approval

Only after desktop and backend work and an explicit propeller-off bench
check: physically attend the disarmed aircraft, enable the reviewed
Pi-side `AEROHELM_SUPERVISED_CALIBRATION_ENABLED=true`, request one of
GYRO/BAROMETER/ACCEL_SIMPLE in OPS and locally approve its unique
request ID on the Pi terminal. Observe actual COMMAND_ACK and status.
Restore the flag to **false** after completion. An accepted ACK is not
proof calibration passed all aircraft prearm checks.

## Functional limitations and future hard blockers

- **Physical autonomous mission execution disabled** even if simulator works.
  Require ArduPilot SITL, local FC geofence, battery/EKF/GPS prearm,
  RC takeover, link-loss policy and independent action authorization.
- **MAVLink two-way protocol** needs a segregated read-only PARAM/MISSION
  transaction proxy. M1 UDP/TCP adapter intentionally drops GCS output.
- **No true multi-aircraft fleet identity**: multiple simulator vehicles can
  be displayed in future, but current backend has one provisioned real Pi ID.
- **No embedded live video**: VISION launcher depends on MediaMTX
  installed/configured and Pi camera actually working. Camera 2 was not yet
  detected on hardware.
- **No accurate terrain/airspace checks** without verified external datasets.
- **No persistent security audit**: HMAC chain rotates/disappears on server
  restart. A trusted append-only external ledger and RBAC/MFA remain future.
- **No Kerberos server on aircraft**; optional SSO only at an enterprise
  operator gateway. The current Tailscale/TLS/JWT scheme stays.
- **No hardware MAVLink2 signing configuration yet**; verify Pixhawk
  compatibility and key rotation before opting in.
- **No public network exposure**: Oracle E2.1.Micro 1GB runs lightweight API
  only, Pi camera WebRTC direct over Tailscale.

## Backend CI

On PR #13 the CI runs API, relay, operations, planning, archive and Pi journal
tests under Python 3.12. The most recent V2 run passed **38 tests**. This is
only software testing; not flight, browser/UI, or FC bench qualification.
