# AeroHelm GCS — full cloud-native Mission Planner development RFC

Status: **Design approved for staged implementation; NOT flight-authorized.**  
Baseline: real Pixhawk 2.4.8 -> Pi 5 GPIO UART0 receive-only -> Oracle Always Free E2.1.Micro FastAPI -> Tailscale Serve -> AeroHelm Mission Planner plugin registry/view. Verified aircraft AH-HW-001, STABILIZE. The native Mission Planner main MAVLink connection is still disconnected.

## Decision

Keep the working upstream Mission Planner MAVLink engine, parameter/mission/calibration screens, and USB/TCP/UDP maintenance options. Build a **single bidirectional binary MAVLink data-plane** via the Pi and a private cloud broker, then present it to Mission Planner through an authenticated **local loopback TCP adapter** (prototype) and ultimately its `MAVLinkInterface` communication-stream abstraction (integrated). JSON telemetry and read-only view sessions remain separate observer APIs, never masquerading as a native GCS connection.

Do **not** inject decoded JSON telemetry into individual HUD widgets. Doing so cannot satisfy Mission Planner's command acknowledgments, PARAM, MISSION, calibrations, status text, and stream negotiation.

## Physical prerequisites

- **For receive-only testing:** Pixhawk verified TELEM1 TX -> Pi physical pin 10 (GPIO15 RX); shared GND to Pi physical pin 9 or 6. Verified UART loopback and real heartbeat. Do not change known-good wiring to match an ambiguous drawing: verify the clone's physical pin numbering with the actual harness and documentation.
- **For bidirectional operation, only after safety gates:** add Pi physical pin 8 (GPIO14 TX) -> verified Pixhawk TELEM1 RX. GPIO/Pixhawk UART signals must be 3.3 V compatible. **NEVER connect Pixhawk TELEM 5V to Pi GPIO.** Power Pi independently. Props removed for bench work.
- `SERIAL1_PROTOCOL=2` MAVLink2, `SERIAL1_BAUD=57` 57600, flow control disabled when RTS/CTS unwired. Verify command/response direction separately before enabling writes.
- Do not open the same serial port with competing agent processes: one link owner, optional fanout for observers.

## Topology

```text
Aircraft (Pi 5)
  Pixhawk 2.4.8 [TELEM1 UART RX/TX + GND]
     <-> aerohelm-linkd (single UART owner; frame inspection and policy)
           |  outbound authenticated WSS, per-aircraft identity
           v
Oracle VM (1 GB E2.1.Micro)
  Tailscale Serve HTTPS/WSS -> loopback API + bounded MAVLink broker
      - JWT-authenticated operator GCS
      - device token / device identity
      - one command-capable owner / many read-only observers
      - no unbounded buffering or replay after reconnect
           ^
           | private WSS
Windows
  AeroHelm desktop cloud transport adapter
     <-> 127.0.0.1:5760 local TCP MAVLink stream
     <-> Mission Planner original MAVLinkInterface engine
       -> Flight Data / HUD / Plan / Setup / Config / Messages

Camera (parallel, NEVER part of MAVLink command queue)
  Pi camera -> native capture + MediaMTX/WebRTC -> authenticated private
  tailnet endpoint -> GCS embedded WebView2 VISION window.
  Direct tailnet video path preferred; do not relay full video through 1 GB VM.
```

**Cloud requirement:** the *primary MAVLink data path* MUST traverse the Oracle broker to count as cloud brokered; a direct Tailscale UDP path is an optional low-latency diagnostic/fallback, not the claimed cloud implementation.

## Functional capability and gates

| Function | Feasible | Gate/limitation |
| --- | --- | --- |
| Native live HUD: attitude, altitude, groundspeed, battery, GPS, mode | Yes | Mission Planner attached to binary MAVLink stream; freshness and real sensor coverage |
| Raw gyro measurements | Yes if streamed | RAW_IMU/SCALED_IMU or suitable MAVLink source; HUD attitude is not raw gyro |
| Plan display/draft | Yes | Local planning works offline |
| Read/download mission | Yes | Bidirectional MISSION protocol and link recovery |
| Write/upload mission | Later | Disarmed bench tests, integrity/ACK/re-read; explicit operator authorization |
| Read parameters | Yes | Bidirectional PARAM protocol, robust download/retry |
| Write parameters | Later | Schema/range checks, disarmed policy for safety-critical settings, audit and verify readback |
| Gyro/accel/compass calibration | Conditional | MAVLink supported; disarmed, on-site operator physically moving aircraft as instructed |
| RC calibration | Conditional | Physical radio/receiver and stick movement required; remote GCS UI can coordinate only |
| ESC/motor tests | Not enabled by default | Local-only maintenance, with props removed; deny through cloud in operational profile |
| Firmware upgrade/DFU/USB bootloader | No generic transparent-cloud guarantee | Provide supervised local USB maintenance workflow |
| Flight actuation/joystick | Deliberately disabled | Controlled SITL/fault tests, RC priority, geofence, stale authority cap, failsafe test, security audit |
| Live onboard video | Yes | Pi 5 software H.264 encoding/low-latency, MediaMTX and WebRTC, private overlay ICE connectivity |
| Dual camera | Later | Second IMX708 detection must be verified; bandwidth and CPU qualification required |

## Security invariants

1. HTTPS/WSS with certificate validation; tailnet ACL; never public 8000/8080/5760/14550; no Tailscale Funnel.
2. Authentication and authorization are distinct: a cloud login does **not** authorize arming, flying, param writes or actuator tests.
3. MAVLink2 signing where supported; signing is integrity/authentication, **not encryption**. TLS protects transport.
4. Cloud operator session + per-aircraft exclusive command lease + explicit Pi-local safety policy; independent real RC/pilot authority.
5. Track frame source system/component, vehicle identity, message sequence, monotonic age, auth expiry; reject stale commands, duplicates, cross-vehicle frames and command replay after outages.
6. One serial owner on Pi. Queue bounded with telemetry drop policy (newer replaces older if appropriate), never silently drop/duplicate critical command ACK transactions.
7. No network-reconnect burst of queued commands. On link timeout revoke control authority, quarantine outstanding commands, alert GCS and require a fresh operator action; do not promise flight safety without FC-side failsafe.
8. Do not rely solely on GCS HEARTBEAT; a Pi proxy may continue heartbeat despite upstream operator loss. Verify ArduCopter GCS failsafe behaviour and explicit onboard watchdog separately.
9. Full command channel remains **disabled** until HITL/SITL, propeller-off FC bench, remote human supervision and failsafe acceptance.
10. No plain-text shared secret in code, UI screenshot, GitHub, logs or persistent insecure config. Windows credentials: OS-protected store; tokens memory only as feasible.

## UI / product

- Product label: **AeroHelm GCS**; startup modal **AeroHelm Cloud Sign In**; connection states: Signed out, Cloud online, Aircraft online, MAVLink linked, Viewer only, Authorized maintenance (explicit).
- User is authenticated before operational tabs become available. Preserve a separate **Local Maintenance** path for offline recovery/firmware/diagnosis rather than destructively removing upstream local links. Do not claim login is an FC-side security boundary.
- Default operational profile connects via Cloud and selects the authenticated aircraft, then attaches real MAVLink stream to the native Flight Data panel. Show latency, last HEARTBEAT age, packet rate, source ID, cloud status, serial status, and failsafe state; explicitly indicate stale telemetry.
- Toolbar **VISION** with camera icon beside **CLOUD**. On demand, open an embedded WebView2/standalone viewer (first available Pi camera) with video status, reconnect, full-screen, bitrate and latency; support camera switching only after camera 2 is detected.
- Preserve native Flight Data, Flight Plan, Setup, Config and maintenance screens; enable dangerous operations only after explicit safety qualification. Do not redesign these screens before the transport works.
- Upstream branding hooks: `Program.cs` supports runtime `logo.txt` (app name), `logo2.png` (top toolbar logo), `icon.png`, `splashbg.png`. `MainV2.cs` uses `Program.Logo2` to override `MenuArduPilot.Image`. Use a properly licensed user-supplied AeroHelm logo; don't fake or silently recreate an existing trademark. Audit other user-facing strings separately.
- Branding changes must NOT strip copyright notices, license texts, source links or GPL-3.0-or-later obligations. Preserve attribution in About -> Open Source Licenses. The user's "remove Mission Planner names" request applies to product-facing title/menus, not legal notices or C# namespace compatibility. Consider settings migration when product name changes because `logo.txt` also changes settings XML filename.

## Component implementation map

**Pi**:
- `agent/linkd.py` (new): one robust UART reader/writer with MAVLink2 frame validation, heartbeats, backpressure, telemetry fanout, local safety and outbound authenticated WSS. Existing `agent/main.py` remains the receive-only fallback until replacement is tested.
- Never enable Pi TX unless a command class is explicitly supported in a stage. Show actual ACKs without inventing success.

**Oracle backend**:
- `backend/app/relay.py` and protocol tests (new): per-vehicle authenticated WSS broker, separate device and operator channels, strict single-command-owner, binary-frame cap, bounded queues, disconnect fencing and counters.
- Run single-worker on E2.1.Micro, benchmark memory and data rate; do not deploy heavyweight Next.js or WebRTC media relay on this 1 GB VM.
- `/health` stays, add meaningful `/api/v1/link-health` with link freshness/latency and no credentials.

**Windows**:
- `Plugins/AeroHelmCloud/Transport` (new or separate assembly): WSS client, local TCP listener on 127.0.0.1:5760 for first MVP, reconnect state. Then integrate with Mission Planner's native `MAVLinkInterface` stream selection; no fake HUD updates.
- Cloud plugin: startup sign-in gateway, auto stream attachment after selecting an online aircraft, VISION launcher, operator/aircraft states and session expiry handling.
- Branding pack deploys runtime overrides without changing core functionality.

**Test plan**:
- Unit protocol tests, heartbeats and corrupted/fragmented MAVLink2 framing, auth tokens, device identity/expiry, duplicate owner conflicts, mixed vehicle systems.
- Local SITL -> cloud -> Windows native MP: HUD updates without COM radio, read params, receive COMMAND_ACK for safe read-only requests, download mission, verify calibration prompts with simulation.
- Propeller-off real FC bench: unplug Pi UART, kill Pi agent, drop tailnet, pause internet, restart cloud, expire operator token, vary WAN latency/loss and make sure all command lanes fail closed.
- Measure end-to-end latency, packet loss, server RSS/CPU, link recovery times, camera FPS and glass-to-glass delay. Don't assert numerical targets without measurements.
- Flight command actuation deferred; only after documented physical pilot RC takeover and failsafe tests.

## Milestone sequence / objective completion

- **M1: Native display receive-only cloud link**: Pi -> Oracle -> Windows 127.0.0.1:5760 -> MP HUD; telemetry visible with no COM or radio.
- **M2: Full duplex safe read-only transactions**: add physical TX third wire and broker response path; query versions/params and download mission; no actuator commands.
- **M3: Supervised disarmed configuration/calibration**: safe parameter writes with readback, mission upload with verify, selected onboard calibration. Local operator must physically move UAV.
- **M4: VISION camera**: one live camera Pi -> WebRTC viewer; 2nd camera conditional.
- **M5: Desktop sign-in, branding, packaging**: AeroHelm GCS startup, automatic cloud connect, local offline maintenance, proper logo/icon and installer.
- **M6: Safety qualification for flight controls**: SITL first, then bench-only command/ACK, independent Pi/FC fail-safes, RC override, geofence, network fault injection. Only after separate go/no-go decision.

## Sources verified (2026-11-01)

- https://docs.ardupilot.org/dev/docs/mavlink-calibration.html
- https://ardupilot.org/dev/docs/mavlink-get-set-params.html
- https://ardupilot.org/copter/docs/gcs-failsafe.html
- https://software.ardupilot.org/mavproxy/docs/getting_started/starting.html
- https://en.ardupilot.org/dev/docs/using-sitl-for-ardupilot-testing.html
- https://www.raspberrypi.com/documentation/accessories/camera-software.html
- https://github.com/bluenviron/mediamtx/blob/main/docs/2-features/25-webrtc-specific-features.md
- https://github.com/ArduPilot/MissionPlanner (GPL-3.0)
- https://ardupilot.org/dev/docs/trademark.html
