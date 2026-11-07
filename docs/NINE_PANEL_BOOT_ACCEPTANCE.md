# AeroHelm boot-to-ready and nine-panel hardware acceptance

Status: implementation is ahead of hardware validation. Native Mission Planner
Touch has opened on Windows, but no nine-panel end-to-end qualification is
complete. Test with the Pixhawk disarmed and ALL propellers removed.

## One Windows session launcher

Open PowerShell in C:\Users\saita\AeroHelm-GCS and run:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Session.ps1

The current research development branch starts Mission Planner with a
**single operator cloud login**, then opens TOUCH. Its *native receive-only*
TCP adapter listens on `127.0.0.1:5760`, so the Python TCP bridge is no longer
required by default. Existing CLOUD dialogs reuse the authenticated token.
**Click FLIGHT once** to open native Flight Data and attempt a real Pixhawk
MAVLink heartbeat check without entering a TCP host/port. The local receiver
cannot generate missing HEARTBEAT or ATTITUDE messages and cannot transmit
outbound aircraft commands. The prior external Python adapter remains an
explicit fallback via `-UseLegacyPythonBridge` and needs its own masked login.
Never run both adapters on port 5760 simultaneously. No password is stored.

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

## Toolbar and visual integration

AeroHelm toolbar plugins CLOUD, SURVEY, VISION, LINK, OPS, INTEL and TOUCH
now use deterministic native vector icons (no external font or image assets).
The AeroHelm operator profile hides upstream HELP and SIMULATION toolbar
tiles by default. The original Mission Planner maintenance/SITL functionality
is not uninstalled; `-ShowNativeToolbar` and offline `--local-maintenance`
remain narrow recovery options, never remote flight-control permissions.

## Build and no-data acceptance

Review branch `feat/gcs-research-cybershield-twin-ui`, close Mission Planner,
and run `Inspect-AeroHelmWindowsStage.ps1 -Stage Source`. Only after it
passes should the Windows operator explicitly run `-Stage Build -SkipBranding`.
The build runs actual linked receive-only bridge protocol tests before MSBuild.
All runtime output is backed up by the transactional plugin installer.

A previous successful M1 camera/cloud handshake does NOT establish valid
native HUD. Current Oracle M1 update now has heartbeat/attitude count fields
in source, but the deployed service must be separately upgraded/tested.
Expect `heartbeat_frames_received` and `attitude_frames_received` to rise;
watch the native GCS receive-only bridge's decoded real roll/pitch values
while gently moving an unarmed, propeller-less FC. If HB=0, investigate
Pi UART and FC heartbeat before touching parameters or stream rates.
All LIVE hardware acceptance remains pending.
