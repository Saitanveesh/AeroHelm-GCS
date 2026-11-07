# Vihanga GCS — cloud attitude acceptance

Scope: disarmed, propellers removed / no vehicle command execution.
This is a diagnostic procedure, **not** flight certification.

## Why USB working is not cloud verification

The USB connector and the Pixhawk telemetry UART can have different MAVLink
stream rates. Mission Planner connected by USB may receive MAVLink ATTITUDE
(message ID 30) while the Pi's UART only receives HEARTBEAT (ID 0).

Vihanga's native cloud bridge consumes the authenticated private
`/ws/mavlink/view/{vehicle_id}` **binary** stream and exposes it to
Mission Planner through a receive-only loopback TCP server. It cannot
make the flight controller start an absent MAVLink stream: outbound
bytes are intentionally discarded. The independent cloud attitude monitor
also consumes this WSS stream. It no longer relies on the optional
`/api/v1/mavlink/attitude` HTTP route.

## Minimum disarmed acceptance sequence

1. **USB reference:** With the aircraft disarmed and no props, verify the
   built-in Mission Planner attitude follows gentle roll/pitch movement over
   direct USB. Do not use this observation to imply Pi UART readiness.
2. **Pi UART owner:** Inspect the existing Pi UART-owning agent's per-message
   counters on the *same physical UART* that feeds the cloud. Check for
   genuine MAVLink ID 0 (HEARTBEAT) and ID 30 (ATTITUDE). Do not open the
   serial port in a second process or replace its owner.
3. **Stream configuration:** If ID 0 is present but ID 30 is absent at the
   Pi UART, correct the **FC telemetry-port stream configuration on site**,
   not via the cloud GCS. On ArduPilot this can be a correct channel's
   `SRx_EXTRA1` rate or an explicitly authorized
   `MAV_CMD_SET_MESSAGE_INTERVAL` request by the UART owner. The index
   `x` depends on the board/firmware/MAVLink channel. Do not guess and
   do not alter settings on an armed aircraft. Official guidance:
   https://ardupilot.org/dev/docs/mavlink-requesting-data.html
4. **Private cloud WSS:** Open PATHS → Attitude monitor after signing in.
   A successful connection reports received MAVLink frames and separate
   HB/ATT counters. No ATT despite verified Pi UART ATT indicates an
   upstream relay/subscription/filter problem. An HTTP 404 from an optional
   attitude-preview API is not evidence that WSS is healthy or unhealthy.
5. **Native Flight Data:** Disconnect any active direct COM/USB link
   deliberately and choose PATHS → Cloud telemetry. Check LINK's
   native-bridge summary. It must report a live HB **and** live ATT with
   real roll/pitch changing as the disarmed vehicle is tilted. If WSS
   monitor sees ATT but native bridge does not, investigate the local
   127.0.0.1:5760 TCP listener/stream and native connection.
6. **Fail closed:** Disconnect Pi telemetry and verify both the monitor and
   LINK mark ATTITUDE stale. The old roll/pitch must not be presented as
   current cloud evidence. Cloud reconnect must not switch to pilot radio
   automatically and must not issue flight commands.

## Restore cable-like telemetry on the *Pi* MAVLink port

The present monitor has observed real WSS HEARTBEAT but **zero**
ATTITUDE messages. This is neither a WinForms horizon-drawing error
nor evidence that the Pixhawk is unresponsive. It is evidence that
message ID 30 was not delivered by the telemetry stream.

Do the following with propellers removed and the vehicle disarmed:

1. Connect the **real Pixhawk via USB** to native Mission Planner,
   keep the existing Pi agent running and note exactly which
   physical FC TELEM/SERIAL port is wired to the Pi. Do not open the
   Pi serial device from a second program.
2. In **Config/Tuning → Full Parameter List** record
   `SERIALx_PROTOCOL`, `SERIALx_BAUD`, and the MAVLink telemetry
   channel corresponding to that Pi-wired physical port.
   The MAVLink rate prefix is **not necessarily the same number**
   as `SERIALx`; recent ArduPilot uses `MAVn_` and some firmware
   uses `SRn_`. Do not assume `SERIAL1` means `MAV1`.
3. Inspect the confirmed Pi channel's `MAVn_EXTRA1`
   (or `SRn_EXTRA1`) **ATTITUDE stream**. If zero, the
   FC will not proactively send roll/pitch on that link.
   For a properly provisioned bench link a *provisional* 5 Hz
   ATTITUDE-group rate is a useful visible-motion baseline.
   Confirm baud/bandwidth and restore the previous value if the
   link is degraded. No code here modifies FC parameters.
4. To approach the cable's broader flight-display coverage,
   inspect `MAVn_EXT_STAT` (system/battery/GPS), `MAVn_POSITION`,
   `MAVn_EXTRA2` (VFR_HUD), `MAVn_EXTRA3` (other state),
   `MAVn_RAW_SENS` and `MAVn_RC_CHAN` for **that same
   MAVLink channel**. Do not blindly bulk-write rates across links.
   The overall bandwidth and CPU budget must be respected.
5. Save the approved settings via the **physically connected USB**
   GCS, reboot the FC if required by the firmware, and check that
   ID 30 now arrives on the Pi UART. If it does, but the cloud
   monitor remains at ID 30 = 0, the Pi forwarding/Oracle relay
   is filtering or losing attitude packets.
6. Only once the cloud monitor sees moving, CRC-verified ID 30
   should the native PATHS → Cloud telemetry HUD be accepted.

References: [ArduPilot MAVLink advanced configuration](https://ardupilot.org/copter/docs/common-mavlink-configuration.html),
[ArduPilot telemetry port setup](https://ardupilot.org/copter/docs/common-telemetry-port-setup.html).
These are firmware-dependent parameters; inspect the live flight
controller, not a generic board template.

**Operational limitation:** The Vihanga cloud path is intentionally
receive-only. This can mirror live HUD/map/system telemetry after the
correct upstream rates exist; it cannot replace USB for parameter
writes, mission uploads, calibrations or commands until a separate
reviewed, authenticated and safety-accepted bidirectional control
architecture is actually implemented.

## Decision table

| Pi UART ID 30 | WSS attitude monitor ID 30 | Native bridge ID 30 | Diagnosis |
| --- | --- | --- | --- |
| 0 | 0 | 0 | FC-to-Pi port stream configuration or UART input |
| Positive | 0 | 0 | Pi uplink or cloud WSS relay/filter |
| Positive | Positive | 0 | Windows native TCP listener/bridge connection |
| Positive | Positive | Positive | Verify native HUD freshness and moving roll/pitch |
| Unknown | 0 | 0 | Upstream status unknown; collect Pi UART counters before altering anything |

An HTTP 200, a generic frame count, a heartbeat, or a UI status label is **not**
proof of live attitude. A live indicator requires real CRC-verified MAVLink
ATTITUDE data with bounded freshness. Keep source acceptance, release-hash
verification, Windows UI testing, live Pi evidence and disarmed hardware
acceptance as separate gates.

Do not paste operator JWTs, network credentials, or hardware serial numbers
into issue reports. Message-ID counts, timestamps, error categories and
sanitized build logs are sufficient for diagnosis.
