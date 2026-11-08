# Vihanga private MAVLink link

This directory contains the **current single-aircraft physical telemetry transport**. It connects a Pixhawk to a Raspberry Pi, an Oracle VM and an authenticated Windows GCS viewer over a private Tailscale network.

**Current capability:** real flight-controller telemetry and restricted, supervised telemetry diagnostics. **Not implemented:** cloud arming, takeoff, mode changes, mission execution, arbitrary MAVLink forwarding or USB-equivalent remote flight control.

## Files

| File | Purpose |
| --- | --- |
| `pi_agent.py` | Exclusively read the physical Pixhawk UART; forward original verified MAVLink messages |
| `relay.py` | Authenticate the Pi and operator viewers, validate frames, record freshness and expose private health |
| `oracle_identity.py` | Check the exact Oracle Compute instance identity |
| `deploy/install_pi.sh` | Install the Pi service and dependencies without starting it |
| `deploy/configure_pi.py` | Write Pi-only device token and configuration |
| `deploy/install_oracle.sh` | Install the private Oracle relay and dependencies without starting it |
| `deploy/configure_relay.py` | Configure relay identity, separate operator login and device-token digest |
| `deploy/preflight_pi.sh` | Refuse unsafe UART ownership or serial-console conditions |
| `deploy/verify_link.py` | Check real MAVLink messages from a private viewer |
| `deploy/validate_full_duplex.py` | Verify Oracle identity, Pi round-trip ping and actual FC telemetry; optional isolated bench probe |
| `deploy/probe_uart_readonly.py` | Inspect a UART without transmitting any commands |
| `mission_preview.py` | Bounded simulation preview, not dispatch |
| `simulation_workflow.py`, `supervised_control.py` | Simulator-only planning and authority model |
| `test_*.py` | Offline protocol and contract tests; no physical aircraft required |

## Data path

```text
Pixhawk / ArduPilot
   |  MAVLink serial bytes
   v
Raspberry Pi : /dev/ttyAMA0 (verified example; not universal)
   |  vihanga-pi.service
   |  authenticated, private WSS
   v
Oracle VM : 127.0.0.1:8765
   |  vihanga-relay.service, exposed only via Tailscale HTTPS :8443
   v
Windows Mission Planner / Vihanga GCS
   |  signed-in, receive-only viewer
   v
Native HUD, Quick data, link diagnostics
```

On the deployed Pi 5 test, `/dev/ttyAMA0` carried the Pixhawk data at 57,600 baud. `/dev/ttyAMA10` was the Pi's debug UART, not the wired flight-controller port. Always verify the actual physical port before starting the agent. The old `aerohelm-agent@` service and `vihanga-pi.service` must never run simultaneously on the same UART.

## Set up and operate

Use [the full Pi–Oracle deployment runbook](../../docs/VIHANGA_ORACLE_PI_LIVE_LINK_DEPLOY.md). The install scripts put service files under systemd, and the configure scripts create protected environment files and device credentials.

The Pi's secret token remains on the Pi. Only its SHA-256 **digest** is entered into the Oracle relay. The Oracle operator login uses a separate password; the Pi token must not be sent to Windows. The relay compares its configured Oracle instance identity to local Oracle metadata before serving private viewers.

Two separate services are involved:

```bash
# On the Raspberry Pi — ONLY after UART safety preflight:
sudo systemctl status vihanga-pi.service --no-pager

# On the Oracle VM:
sudo systemctl status vihanga-relay.service --no-pager
curl -fsS http://127.0.0.1:8765/health
```

The Oracle VM does not have the Pixhawk UART or the `vihanga-pi.service` unit. The Raspberry Pi does not host the `vihanga-relay.service` unit. Do not mix their commands.

## Confirm actual telemetry

Check the private relay on an authorized tailnet device:

```bash
curl -fsS https://YOUR-TAILSCALE-HOST.ts.net:8443/api/v1/mavlink/health
```

Healthy native HUD telemetry requires fresh **HEARTBEAT (ID 0)** and **ATTITUDE (ID 30)**, plus position **ID 33** or VFR_HUD **ID 74** where available. Report GPS or other values as unavailable if the FC does not provide them. The health endpoint reports message counts, observed rates and age instead of synthesizing missing values.

During the 8 November 2026 operator bench test, the Pi and Oracle measured heartbeat at about 1 Hz, attitude at about 5 Hz, and position and VFR_HUD at about 2 Hz. The private round-trip ping returned 286 ms in one test. These values are measurements, not service-level guarantees.

For an authenticated read-only validation on Oracle:

```bash
cd /opt/vihanga/source
/opt/vihanga/venv/bin/python -m services.vihanga_link.deploy.verify_link \
  --origin https://YOUR-TAILSCALE-HOST.ts.net:8443 \
  --vehicle AH-HW-001 --operator operator --seconds 12
```

That command prompts privately for the independent relay password. It does not send aircraft commands.

## Why the HUD may initially show only a heartbeat

ArduPilot may not publish all message streams on every serial port without a stream-rate request. On a **physically disarmed, propellers-removed bench**, and only with explicit local authorization, the Pi can request these nonpersistent rates:

| Message | MAVLink ID | Requested rate |
| --- | ---: | ---: |
| ATTITUDE | 30 | 5 Hz |
| GLOBAL_POSITION_INT | 33 | 2 Hz |
| VFR_HUD | 74 | 2 Hz |
| SYS_STATUS | 1 | 1 Hz |
| GPS_RAW_INT | 24 | 1 Hz |
| BATTERY_STATUS | 147 | 1 Hz |

The Pi issues `MAV_CMD_SET_MESSAGE_INTERVAL` (511) only when a verified ArduPilot heartbeat identifies the controller as **disarmed** and the opt-in is active. A physical `COMMAND_ACK` with result 0 confirms that the FC accepted the request, but actual ATTITUDE frames must still be observed. These rates may disappear after FC power loss. **Do not keep maintenance opt-ins enabled for ordinary operations.**

The private cloud request channel accepts only `link_ping` and a restricted `disarmed_stream_probe` diagnostic. It cannot accept raw MAVLink from a Windows client, and it does not expose a physical flight-control API.

## Fault handling

| Symptom | First check |
| --- | --- |
| Cloud health says `device_online=false` | Pi service journal, private tailnet reachability, device authentication |
| Cloud says device online but frames are zero | UART pins, correct serial device, verified baud, physical Pixhawk power |
| HEARTBEAT arrives but ATTITUDE is zero | Per-port FC stream rate on an approved disarmed bench |
| Pi sees ATTITUDE but Oracle does not | WebSocket stability, packet rejection, stale queue and identity configuration |
| Oracle receives ATTITUDE but Windows HUD does not | Native viewer sign-in, correct relay origin and Windows bridge connection |
| After reboot, attitude messages disappear | Revalidate FC output interval policy rather than assuming volatile requests persist |
| UART is occupied | Inspect its owner; never terminate unknown Python processes or start a second reader |

For the full acceptance matrix and release boundaries, see [Vihanga project status](../../docs/AEROHELM_STATUS.md).

## Flight-control source development

The separate [supervised flight-control design](../../docs/VIHANGA_SUPERVISED_FLIGHT_CONTROL.md)
includes pilot/approver leases and independently gated MAVLink command and
mission/parameter transaction models. **None of these source modules is
connected to the running Pi UART publisher or native receive-only Windows
bridge.** Ordinary flight telemetry remains the supported production path.

## Security and constraints

The relay binds only to the local VM loopback, behind private Tailscale HTTPS. Authentication uses a separate Pi credential and operator JWT. Access control and freshness limits do not replace flight-controller or pilot failsafes. The native cloud path is receive-only for aircraft flight control. No source file in this directory certifies a remote flight system for operational use.
