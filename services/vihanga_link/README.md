# Vihanga private cloud MAVLink transport

This is a new, independently deployable telemetry service. It is NOT the
running Oracle M1 service or the existing Pi UART agent; writing this source
does not deploy it or make the current aircraft cloud link functional.

## Data path

    Pixhawk TELEM UART
      -> sole Pi serial owner (services/vihanga_link/pi_agent.py)
      -> private device WSS -> private relay (services/vihanga_link/relay.py)
      -> short-lived operator-authenticated viewer WSS
      -> Windows receive-only 127.0.0.1:5760 -> Mission Planner native HUD

Full CRC-parsed MAVLink frames are forwarded unchanged. The cloud relay
never receives data from the Windows GCS for forwarding to the flight
controller. Its viewer tokens are restricted to one aircraft and expire
after ten minutes. A dropped connection cannot replay old queued telemetry.

## Relay provisioning (not automatic)

Use Python 3.12, a private Tailscale HTTPS proxy and secret storage.
Do not expose the application directly to the public Internet.

    python3 -m venv .venv-vihanga
    . .venv-vihanga/bin/activate
    python -m pip install -r services/vihanga_link/requirements-cloud.txt

The service requires these variables from a protected private environment:

- VIHANGA_VEHICLE_ID: explicit aircraft ID (example AH-HW-001)
- VIHANGA_ALLOWED_HOSTS: exact private gateway hostname, not a wildcard
- VIHANGA_OPERATOR_NAME: dedicated operator username
- VIHANGA_OPERATOR_PASSWORD_HASH: Argon2id password hash, NOT plaintext
- VIHANGA_JWT_KEY: independent cryptographically random key, 32+ bytes
- VIHANGA_DEVICE_TOKEN_SHA256: hex SHA-256 of random Pi-only device secret

Generate the operator hash without exposing the password in process arguments:

    python -c "import getpass; from argon2 import PasswordHasher; print(PasswordHasher().hash(getpass.getpass()))"

Store the raw Pi token in a file with mode 0600. Never commit credentials.
Run the relay behind the approved private HTTPS reverse proxy:

    python -m services.vihanga_link.relay

The process binds 127.0.0.1:8765. Proxy these routes at a single exact host:

- /health
- /api/v1/auth/login
- /api/v1/mavlink/health
- /api/v1/mavlink/paths
- /ws/mavlink/device/{vehicle_id}
- /ws/mavlink/view/{vehicle_id}

Protect the gateway with tailnet ACLs. JWT verification does not replace
network identity or a firewall. Do not give the device token to Windows
clients or the operator JWT to the Pi.

## Pi provisioning (disarmed, propellers removed)

Use the physically confirmed serial interface and baud, not generic
example defaults. The old serial owner must be stopped by an operator
and verified stopped. The new agent refuses startup if fuser reports the
port in use. Never start a second UART reader.

    python3 -m venv .venv-vihanga-pi
    . .venv-vihanga-pi/bin/activate
    python -m pip install -r services/vihanga_link/requirements-pi.txt

Set the following private variables (the sample values are illustrative):

    VIHANGA_PI_SERIAL=/dev/ttyAMA0
    VIHANGA_PI_BAUD=57600
    VIHANGA_VEHICLE_ID=AH-HW-001
    VIHANGA_FC_SYSID=1
    VIHANGA_DEVICE_WS_URL=wss://YOUR-PRIVATE-RELAY.ts.net/ws/mavlink/device/AH-HW-001
    VIHANGA_PINNED_RELAY_HOST=YOUR-PRIVATE-RELAY.ts.net
    VIHANGA_DEVICE_TOKEN_FILE=/etc/vihanga/device-token
    VIHANGA_DISARMED_STREAM_PROBE=1

Run:

    python -m services.vihanga_link.pi_agent

The local-only, opt-in stream probe uses MAV_CMD_SET_MESSAGE_INTERVAL
when an identified ArduPilot heartbeat proves disarmed. It requests
non-persistent rates: ATTITUDE 30 (5 Hz), GLOBAL_POSITION_INT 33 (2 Hz),
VFR_HUD 74 (2 Hz), SYS_STATUS 1 (1 Hz), GPS_RAW_INT 24 (1 Hz),
BATTERY_STATUS 147 (1 Hz). It never modifies persistent parameters
or enables remote commands. If this process has observed an armed
heartbeat, no rate probe is attempted in that session.

This cannot repair wrong wiring, serial baud/protocol, a broken UART
owner, an FC that ignores interval requests, or a missing relay deployment.

## Windows source update and launch

Close all Mission Planner/Vihanga processes before rebuilding:

    cd "$HOME\Vihanga-GCS"
    git pull --ff-only origin feat/vihanga-phase3-native-toolbar
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-VihangaVerified.ps1 -Apply

Launch the verified build with the new dedicated private relay origin:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 -NoAutoTcp -LinkOrigin https://YOUR-PRIVATE-RELAY.ts.net

This does not replace Oracle M1 authentication. The PATHS connection
prompts for a separate new relay login when LinkOrigin is configured.
Open PATHS -> Attitude monitor to observe actual message IDs. Then
choose PATHS -> Cloud telemetry to connect the native HUD. Disconnect
a previous USB/radio connection deliberately; automatic link transfer
is disabled.

## Tests and release acceptance

    python -m pip install -r services/vihanga_link/requirements-test.txt
    python -m pytest -q services/vihanga_link/test_link.py

These are synthetic offline tests, NOT Oracle deployment tests or
physical aircraft acceptance.

Acceptance gates:
1. The sole Pi UART reader counts verified real HB (0) and ATTITUDE (30),
   plus relevant position/GPS/battery IDs, with correct stream rates.
2. Authenticated Pi WSS forwards real raw frames to the private hub;
   the operator viewer receives the identical bytes and count.
3. Native Windows Mission Planner HUD and attitude monitor follow
   disarmed prop-off vehicle movement and mark interruption stale.
4. Zero replay of stale frames after a drop/reconnect; exclusive UART
   ownership, no auto-radio switchover, and JWT expiration enforced.
5. Windows compiled EXE/DLL and source commit match the release manifest.
6. Arming, mission upload, parameter mutation, actuator commands,
   automatic flight authority and autonomous flight are NOT enabled
   by this telemetry-only development service.

Providing full USB-equivalent COMMAND and AUTONOMY authority requires
separate supervised, audited bidirectional control with onboard failsafe,
geofence, command TTL/idempotency and hardware acceptance. This telemetry
transport alone cannot satisfy that separate milestone.

Do not paste JWTs, private endpoints, passwords or device secrets into
public bug reports.
