# AeroHelm boot-to-ready and nine-panel hardware acceptance

Status: implementation is ahead of hardware validation. Native Mission Planner
Touch has opened on Windows, but no nine-panel end-to-end qualification is
complete. Test with the Pixhawk disarmed and ALL propellers removed.

## One Windows session launcher

Open PowerShell in C:\Users\saita\AeroHelm-GCS and run:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Session.ps1

This starts the backend receive-only TCP bridge when missing, then launches
Mission Planner Touch. The backend checkout must remain a sibling directory
at C:\Users\saita\AeroHelm. Port 5760 is loopback-only. A masked password
dialog is used by the bridge; CLOUD in the GCS needs a separate login.
No password is stored and native TCP selection remains manual in M1.

## Acceptance matrix (each requires an observed live outcome)

| Panel | Validation required |
|---|---|
| FLIGHT | Native ATTITUDE/roll/pitch movement matching the disarmed real Pixhawk |
| PLAN | Native Mission Planner planning opens; no upload/actuation |
| VISION | IMX708 camera 0 live WebRTC video, reconnect and disconnect behavior |
| OPS | Cloud capabilities GET and explicit hardware flight denial |
| SECURITY | Authenticated posture/audit GET, fail-closed on expired login |
| FLEET | AH-HW-001 registry, correct online/fresh and stale state |
| LINK | Pi to Oracle frame freshness changes; unplugged/offline state detected |
| ANALYTICS | Registered aircraft discovery, history/report GET (no fabricated data) |
| SETTINGS | Persist private cloud/camera URL; reject non-private URLs |

## How to diagnose the frozen native Flight Data horizon

An M1 read-only bridge drops outgoing Mission Planner packets. The autopilot
might not be transmitting ATTITUDE frames without a requested stream rate.
The bridge has passive ten-second counters: HEARTBEAT, ATTITUDE (ID 30),
GLOBAL_POSITION_INT, VFR_HUD, etc. No MAVLink request or flight command is sent.

- HEARTBEAT increasing, ATTITUDE=0: diagnose Pixhawk TELEM1 message rates;
  do not change FC parameters or send requests without a disarmed props-off review.
- ATTITUDE increasing but horizon frozen: inspect changing decoded attitude
  values, frame integrity, TCP transport and native Mission Planner state.
- No frames: check single Pi UART owner, agent logs, authenticated WSS and
  backend freshness before assuming an instrument UI defect.

Do not infer that moving telemetry counters proves attitude is live.
The GCS native HUD must be tested physically. GPS and battery can separately
be absent or uninitialized; zero fields are not automatically a UI bug.

## Pi boot services

The Pi deploys separately from Saitanveesh/AeroHelm M1 branch using the opt-in
scripts/install_pi_autostart.sh installer, which creates a user-scoped systemd
agent and camera unit and enables them for the next boot without starting
them during installation. No Tailscale Funnel or Oracle video relay.

All hardware flight authority remains disabled in this acceptance phase.

## Research lab additions (staged only)

FLEET now has a user-consented offline energy-aware scheduler lab accepting
explicit JSON input for up to 32 user-specified aircraft and 100 tasks;
ANALYTICS has the Twin Lab for held-out reference fitting; SECURITY links
research integrity and cyber-evidence views. They require a separately enabled
V3 backend and must NEVER schedule, upload, arm or execute hardware missions.

These are development features only. Do not use a scheduler proposal to infer
verified airspace, geofence, weather, terrain or battery readiness.
