# Vihanga GCS — system architecture

The project keeps flight stabilization on the Pixhawk and uses a private network to deliver real MAVLink telemetry to the Windows ground station. The Windows application is based on ArduPilot Mission Planner.

## Components and responsibilities

| Component | Runs where | Job |
| --- | --- | --- |
| Pixhawk / ArduPilot | Aircraft | Sensor fusion, flight modes, stabilization and onboard failsafes |
| `vihanga-pi.service` | Raspberry Pi 5 | Exclusive UART reader, MAVLink parsing, authenticated cloud publisher |
| Tailscale private network | Pi, Oracle and Windows | Encrypted, access-controlled transport |
| `vihanga-relay.service` | Oracle VM | Verify device credentials and aircraft identity, check MAVLink frames, serve fresh telemetry to signed-in viewers |
| Native Vihanga plugin and HUD | Windows PC | Connect viewer, show actual flight data, mark missing/stale values |
| Direct USB/telemetry radio | Pilot-controlled local path | Independently selected connection for local backup and maintenance |

```text
Pixhawk ──MAVLink UART──> Pi ──authenticated WSS──> Oracle VM
                                                    |
                                         private HTTPS :8443
                                                    |
                                                    v
                                            Windows Vihanga GCS
                                            native DATA/HUD/Quick

Local USB or radio ───────────────────────> pilot-selected direct link
(Independent backup; not a second reader of the Pi's UART)
```

The current setup uses one physical aircraft. The repository also contains cloud-fleet and simulator experiments; those are not equivalent to a deployed multi-aircraft control system.

## Transport boundary

The Pi service opens the actual Pixhawk serial port. Only **one local process** can own that port; the old `aerohelm-agent@...` service must be stopped before the new service starts.

The dedicated Oracle relay uses loopback `127.0.0.1:8765` and is exposed privately by Tailscale on HTTPS `:8443`. The older AeroHelm Oracle API uses `:443` and remains separate. Windows viewers authenticate independently; the device credential is never handed to the Windows application.

For private telemetry, the relay validates packet format and records the observed message ID and time. The native bridge forwards only received data to the Mission Planner HUD. Freshness checks prevent a disconnected feed from being represented as an active connection.

## What can be sent from cloud to Pi?

The deployed private-link implementation permits two restricted diagnostics:

1. **Link ping:** the relay asks the Pi for a matching pong. It measures private transport response, not aircraft control.
2. **Disarmed telemetry-rate request:** if explicitly enabled at both ends and a fresh identified disarmed ArduPilot heartbeat is present, the Pi can issue predefined temporary `MAV_CMD_SET_MESSAGE_INTERVAL` requests. These request message rates; they do not change persistent parameters.

The relay does **not** offer a general MAVLink control socket, arbitrary command injection, arming, mission upload or takeoff through this path. `supervised_control.py` and some `cloud/` workflows model future command interlocks **in simulation only**.

Physical USB-equivalent remote command capabilities would require an independently designed command boundary, authorization, flight-controller acknowledgments, loss-of-link procedures and SITL/HIL/bench qualification. Nothing here treats a successful network response as proof of physical execution.

## Service lifecycle and recovery

| Event | Expected action |
| --- | --- |
| Pi UART is already opened | Refuse a second owner; inspect the known service before changing ownership |
| Oracle unavailable | Reconnect device WSS; avoid replaying stale buffered frames |
| No ATTITUDE from FC | Report it missing rather than inventing HUD movement; verify FC stream configuration |
| Viewer loses cloud connection | Mark samples stale and restore only after fresh verified frames |
| FC reboots | Re-establish verified stream rates through a reviewed policy; current temporary requests may not persist |
| Cloud unavailable but pilot needs control | Use independently tested, explicitly selected local USB/radio connection |

## Repository components

- `MainV2.cs`, `GCSViews/`, `ExtLibs/`: upstream Mission Planner native application and dependencies. Do **not** delete as obsolete by name alone.
- `plugins/` and `Plugins/AeroHelmCloud/`: Vihanga Windows UI, authentication, diagnostic viewers and plugin project.
- `services/vihanga_link/`: current Pi and Oracle private telemetry path; test and deployment utilities.
- `cloud/`: separate fleet scheduling, analytics and simulator-oriented research, **not** the current private relay.
- `scripts/`: Windows build, source contracts, installed-runtime verification and launch.
- `docs/`: current runbooks and historical engineering notes.

## Verification evidence and release discipline

See [status](AEROHELM_STATUS.md) for observations reported from the real November 8, 2026 bench test, and [the deployment runbook](VIHANGA_ORACLE_PI_LIVE_LINK_DEPLOY.md) for commands.

Every Windows release is tied to the exact Git revision and compiled EXE/DLL hashes. Failure to build causes restoration of the previous runtime. Never edit an old manifest to simulate a successful build. A successful unit test, cloud heartbeat or screenshot does not replace an on-site hardware acceptance test.

The original ArduPilot Mission Planner licensing and attribution are preserved. See [provenance](AEROHELM_PROVENANCE.md) and [GPL-3.0 license](../COPYING.txt).
