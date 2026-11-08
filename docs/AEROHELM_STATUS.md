# Vihanga GCS — implementation and test status

**Updated:** 8 November 2026  
**Repository:** `Saitanveesh/AeroHelm-GCS` (`master`)  
**Product name:** Vihanga GCS

This file distinguishes **source implemented**, **operator-observed hardware evidence**, and **work not yet accepted**. It is not a flight-safety approval.

## Current system

The tested configuration has one Pixhawk, a Raspberry Pi 5, an Oracle Cloud VM and a Windows Mission Planner-based ground station. The Pi is the only process reading the Pixhawk UART. The Pi forwards original MAVLink frames to a private relay on Oracle. A signed-in Windows client displays those frames.

| Capability | Implementation | Evidence as of 8 Nov 2026 | Remaining acceptance |
| --- | --- | --- | --- |
| Pixhawk serial telemetry | `vihanga-pi.service` using `/dev/ttyAMA0`, baud 57600, SYSID 1 | Pi log measured heartbeat at ~1 Hz; no competing UART reader | Restart/port ownership recovery |
| Private Pi–Oracle transport | Authenticated WebSocket over Tailscale HTTPS :8443 | Oracle reported connected device and valid real MAVLink frames | Longer outage, loss and reconnection soak tests |
| Cloud-to-Pi diagnostic channel | Fixed `link_ping` / `link_pong` intent | 286 ms round trip observed in operator test | Long-run RTT/packet-loss distribution |
| Physical stream-rate negotiation | Opt-in, disarmed-only `MAV_CMD_SET_MESSAGE_INTERVAL` | Six FC `COMMAND_ACK` responses with result 0; ATTITUDE ~5 Hz, position/VFR_HUD ~2 Hz | Reliable rates after FC reboot; policy for deployment |
| Native Windows telemetry display | Native MAVLink viewer and Mission Planner integration source | Operator screenshot showed altitude, mode, yaw, vertical speed and other Quick values | Confirm current EXE/DLL manifest hashes, physical roll/pitch tilt, stale-display and map/GPS behavior |
| Operator authentication | Dedicated relay login, per-device token, Oracle instance identity pinning | Oracle health and read-only validator passed | Rotation, privilege, expiry and recovery drills |
| Independent local pilot backup | Mission Planner's USB/radio path is retained | Source exists; physical switchover not demonstrated here | Tested direct link after cloud disconnect |
| Read-only parameter snapshot | Source path exists | No complete parameter snapshot shown | Controlled disarmed acquisition and completeness proof |
| Cloud autonomous physical flight control | **Not enabled** | Relay reports `flight_control_enabled=false` | Separate design, safety case and SITL/HIL/bench/flight acceptance |
| Multi-UAV fleet operations | Research code in `cloud/` | No live multi-aircraft evidence supplied | Isolation, tenancy, scale and real deployment |
| Kerberos enterprise login | Optional design/reference code | No configured Kerberos realm/KDC shown | End-to-end deployment and tests |

## Evidence from the 8 November 2026 bench session

1. Pi logs: exclusive `/dev/ttyAMA0` reader; repeated ten-second windows with approximately **10 HEARTBEAT**, **50 ATTITUDE**, **20 GLOBAL_POSITION_INT** and **20 VFR_HUD** messages.
2. Oracle relay health: `device_online=true`, `attitude_stream_active=true`, `native_quick_data_ready=true`; one snapshot reported 672 received frames, 252 attitude frames and no rejected frames.
3. Cloud viewer acceptance: a separate viewer received 10 HEARTBEAT, 49 ATTITUDE, 19 position and 19 VFR_HUD frames during its configured observation window.
4. Cloud diagnostic: authenticated Oracle → Pi → Oracle ping returned **286 ms** in one measurement; the diagnostic **did not contact or command the Pixhawk**.
5. Operator screenshot: Vihanga's Mission Planner window showed populated flight data. The screenshot alone does not prove horizon response to physical rotation.

These results were reported from the operator's actual equipment and logs. They are specific to that session, not a guarantee of future availability, network latency or autonomous behavior.

## Important distinctions

- **Heartbeat is not attitude.** A connected service can receive HEARTBEAT while receiving no usable HUD data.
- **Cloud ping is not a flight-controller command.** It verifies one private network round trip.
- **Command 511 ACK is not flight authorization.** The accepted command requested temporary telemetry intervals; it did not arm, change mode or upload a mission.
- **`fresh=false` on an old ACK is not a rejection.** The command's MAVLink result 0 was accepted when received; the ACK naturally ages out.
- **No GPS fix is not a location.** Do not infer aircraft coordinates from a zero or default map position.
- **A source-code change is not a deployed update.** Windows binaries are accepted only after the release manifest matches their exact build and Git revision.

## Recommended next engineering gates

1. Verify native HUD roll/pitch follows physical Pixhawk motion on a disarmed, propellers-removed bench.
2. Prove stream recovery after a Pixhawk power cycle without leaving maintenance flags enabled.
3. Run cloud-loss and recovery tests, observing stale HUD behavior and **manual**, independent backup selection.
4. Verify parameter snapshot completeness and operator access controls.
5. Run SITL/HIL and qualified engineering reviews before designing any physical command authorization. Do not claim USB-equivalent cloud flight control based on telemetry alone.

## Documentation

- [Start here](../README.md)
- [System architecture](AEROHELM_PROJECT.md)
- [Physical Pi/Oracle deployment and verification](VIHANGA_ORACLE_PI_LIVE_LINK_DEPLOY.md)
- [Private transport implementation](../services/vihanga_link/README.md)
- [Windows release and build troubleshooting](VIHANGA_WINDOWS_BENCH_UPGRADE.md)
- [Historical reports and older designs](README.md)
