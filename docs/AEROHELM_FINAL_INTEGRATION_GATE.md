# AeroHelm — Consolidated final-stage integration gates

**Status:** source committed on `feat/gcs-research-cybershield-twin-ui`.
The operator's **previous** Windows native build and SHA-256 verification
passed; the **new changes in this batch must be rebuilt**. This document is
a feature-release checklist, not a declaration of flightworthiness.

## Current root-cause evidence

The operator's 2026-11-04 LINK screenshot showed:
- Oracle `device_online=true`, `fresh=true`, cumulative `frames_received`
  increasing, yet `viewers=0`.
- The old Oracle JSON had no true counts for MAVLink HEARTBEAT (0) or
  ATTITUDE (30). A generic live frame is not enough for the native HUD.
- Mission Planner's direct `TCP CONNECT` reported
  `No Heartbeat Packets Received`; artificial horizon did not move.
- A healthy Pi Tailscale or camera does not prove a verified autopilot stream.

Mission Planner's physical horizon must never be driven by fabricated data.
The backend M1 branch now has independent HB and ATT counters, a short
stale-data window and a **JWT-protected real-attitude** preview under
`GET /api/v1/mavlink/attitude`. The LINK UI renders genuine real roll/pitch
only if that endpoint is available and fresh. Real preview is not an
authorized/certified flight instrument. It does not bypass the native
Mission Planner HEARTBEAT requirement.

## Single command for latest Windows software

First close native Mission Planner (and any Visual Studio builds). From
Windows PowerShell:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git status --short
git pull --ff-only origin feat/gcs-research-cybershield-twin-ui
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-AeroHelmLatest.ps1 -Apply -Launch -StopVerifiedLegacyBridge
```

**Never** use `git reset --hard` or `git clean -fdx`.
`Update-AeroHelmLatest.ps1` accepts only the reviewed branch and clean
tracked state, fast-forwards the source, calls the backed-up
`Repair-AeroHelmWindows.ps1 -Apply`, verifies current EXE and plugin
SHA-256 and then launches the same source-identified GCS. If the old
Windows Python `aerohelm_tcp_bridge.py` is STILL listening on TCP 5760,
the explicit `-StopVerifiedLegacyBridge` checks the executable/script
identity twice before stopping just that process. Other port owners are
never killed. Port 5760 is localhost-only: do not open a router, firewall,
Oracle security group or Tailscale Funnel port to fix a conflict.

The touch panel now fails fast with an actionable message when:
- the native loopback bridge is not ready;
- the updated cloud **definitively** reports a missing real HEARTBEAT.

With legacy Oracle, real heartbeat status is UNKNOWN and the native GCS
continues to enforce its own heartbeat gate, so a connection still may fail
until the backend and Pi stream are verified.

## Operator-visible capabilities

- TOUCH: `FLIGHT / PLAN / VISION / OPS / SECURITY / FLEET / LINK /
  ANALYTICS / SETTINGS`. HELP and SIMULATION controls are hidden in the
  operator toolbar but retained for local maintenance.
- LINK: real heartbeat/attitude rates, viewer count, server age and a
  separate **operator-JWT-protected roll/pitch preview**. The preview
  reads real packets, never guesses the attitude from generic frame counts.
- ANALYTICS: digital shadow, evidence replay, held-out 2D twin calibration,
  and **six-DOF physics lab** with bounded synthetic hover JSON at
  `Tools/fixtures/physics_lab_hover.json`.
- SECURITY: CyberShield heuristic observations, evidence integrity,
  Cloud capabilities and **Kerberos readiness**. No KDC, SPNEGO gateway,
  service keytab or Kerberos authentication is currently provisioned.
- FLEET: observational fleet registry and operator-specified offline task
  scheduling; no dispatch or mission upload.
- VISION: private Pi camera URL. Raw video is not uploaded to the cloud
  research database.

These features are **not simultaneously deployed**. Current Oracle M1
supports telemetry; M2/V3 research endpoints remain opt-in/unavailable
until a separately reviewed deployment. A six-DOF physics sandbox is
not a real calibrated physics twin.

## Controlled Oracle readiness upgrade

The M1 cloud branch has merged source for
`/api/v1/mavlink/health` real HEARTBEAT/ATTITUDE counters and
`/api/v1/mavlink/attitude` operator-only roll/pitch. Oracle's existing
deployment is still legacy until the API image is updated.

Only with an operator physically supervising a **disarmed propeller-off
bench maintenance window**, run the read-only Oracle inspection and review
`docs/ORACLE_M1_HUD_READINESS_ROLLOUT.md` in `Saitanveesh/AeroHelm`.
The guarded Oracle script is
`scripts/Stage-AeroHelmOracleM1.sh`: defaults to inspection; `--apply`
requires explicit maintenance approval, saves an old Docker API image and
private environment backup, rebuilds **only** the API service, verifies
the response schema and attempts an image rollback if unhealthy.
No Raspberry Pi, camera or flight controller processes are changed.

Do not enable V3 research by changing a random `.env` flag. It requires a
private independent evidence HMAC key, SQLite storage/backup policy,
single-worker server and M2 compatibility checks. Do not claim Kerberos
until a real KDC/realm/gateway policy is independently provisioned.

## Acceptance checklist before "fully ready"

1. Windows native MSBuild and plugin-linked unit tests pass **on the
   latest Git commit**, not a previously compiled release.
2. Release manifest SHA-256 matches EXE/DLL and SETTINGS shows loaded
   plugin fingerprint.
3. All nine touch tiles, custom toolbar symbols and V3 research readiness
   views open without errors; login is interactive.
4. Oracle M1 deployed `heartbeat_frames_received` and
   `attitude_frames_received` count **actual** FC frames, with separate
   freshness booleans and no synthetic packets.
5. LINK JWT-only roll/pitch changes consistently with deliberate,
   propeller-off FC tilting when real ATTITUDE packets arrive.
6. Native Mission Planner FLIGHT receives a real HEARTBEAT and its
   artificial horizon follows the same measured real attitude.
7. Cloud disconnect immediately becomes stale; no vehicle commands
   are sent over the receive-only bridge.
8. V3, Kerberos and physics-twin claims remain disabled/unverified
   until their independent rollout/identification gates pass.

**Not yet passed:** latest GCS Windows compile/UI acceptance, actual
Oracle deployment and physical FC bench verification.
