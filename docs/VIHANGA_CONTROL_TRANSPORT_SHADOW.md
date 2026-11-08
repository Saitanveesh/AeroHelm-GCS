# Vihanga: pilot-to-Pi control transport shadow probe

**Status:** Source implementation; simulator-only role authentication; NO aircraft actuation. Existing deployed services and Windows binaries remain unchanged.

## End-to-end path

1. A Windows operator runs scripts/Test-VihangaControlTransport.ps1 and logs in using the separate simulator pilot credential.
2. The pilot JWT authorizes POST /api/v1/vehicles/{vehicle}/control/transport/probe.
3. Oracle sends one fixed, bounded challenge over the existing authenticated Pi device WebSocket.
4. The Pi asynchronous receiver returns PI_RECEIVED_PROBE_NO_UART. It does not access the UART, flight-controller command encoder, mission handler or parameter writer.
5. Oracle validates request ID, random challenge, control epoch, strict ACK schema and a four-second deadline.
6. The pilot polls GET /api/v1/vehicles/{vehicle}/control/transport/probe/{request_id}. PI_ECHO_NO_ACTUATION is a transport receipt, not a flight-controller acknowledgment.

Neither the telemetry viewer JWT nor the safety-approver JWT may initiate this probe. The diagnostic does not require a flight lease because it cannot send any control command. The relay permits only one outstanding request, three per authenticated Pi session and a two-second cooldown. Duplicate, expired, wrong-epoch, malformed or falsely claimed FC/UART acknowledgments are rejected. Disconnect and FC restart invalidate outstanding receipts.

## Explicit release behavior

- Flight-control simulator authority is still default-disabled. The diagnostic is available only under the existing independent-role simulator configuration with VIHANGA_CONTROL_SIMULATOR_ONLY=1.
- GET /api/v1/flight-control/status reports pilot_to_pi_shadow_probe when this source is present and simulator roles are enabled.
- physical_command_dispatch, native_mavlink_command_tunnel and flight_control_enabled remain false.
- The existing native 127.0.0.1:5760 Windows bridge is still receive-only; this diagnostic uses HTTPS and role-specific authentication, not arbitrary native GCS MAVLink commands.
- Current deployed Pi and Oracle services are unchanged until independently reviewed and installed.

## Windows offline/control-plane test

From a Windows checkout, **only against an isolated maintenance relay already configured for simulator-only roles**, run:

    pwsh -NoProfile -File .\scripts\Test-VihangaControlTransport.ps1 -Origin "https://YOUR-PRIVATE-TAILSCALE-HOST.ts.net:8443" -VehicleId "AH-HW-001" -PilotUsername "pilot1"

The script prompts securely for the pilot simulator password and does not log a JWT. It rejects plain HTTP, missing simulator authority and a relay claiming flight dispatch is enabled. An authenticated Pi connection is required; Pixhawk power, arming and motor actuation are neither required nor verified.

## Offline regression

    python -m pip install -r services/vihanga_link/requirements-test.txt
    python -m pytest -q services/vihanga_link/test_control_transport_probe.py services/vihanga_link/test_control_api.py services/vihanga_link/test_link.py

Test cases include role isolation, malformed/extra actions, no UART access, nonce/epoch mismatch, replay, expiry, rate limits, Pi WSS disconnect, and a complete authenticated shadow round-trip.

## Still required for operational cloud flight control

1. Independent Pi-side hardware pilot key, genuine FC-owned RC/EKF/geofence/battery/state evidence, and verified manual radio takeover.
2. Audited, fail-closed typed command transport to the sole UART owner, with physical interlocks and bounded ACK state machines. Shadow probing is **not** command dispatch.
3. Native Windows GCS command UX requiring independent pilot approval, not raw byte forwarding.
4. Mission/parameter integrity readbacks, on-air loss behavior, durable audit trail and site-specific firmware/vehicle validation.
5. Passing SITL, HIL, propellers-removed physical bench and supervised field acceptance gates.

**Do not deploy as a flight-control release or interpret the probe as permission to arm.**
