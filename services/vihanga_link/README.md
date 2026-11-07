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

The Pi and relay both enforce the configured flight-controller system ID.
The relay verifies MAVLink X25/CRC-extra for every recognized ArduPilot dialect
message and rejects unknown or damaged packets before forwarding intact frames.
NaN, infinite or out-of-range ATTITUDE angles cannot mark the HUD live.
MAVLink2 frame CRC does not authenticate an optional MAVLink2 signature.
The relay never forwards arbitrary Windows MAVLink to the aircraft. An
**explicitly opt-in disarmed telemetry-request duplex** can carry only a
fixed `disarmed_stream_probe` intent to the authenticated Pi, where a
fresh identified DISARMED heartbeat, a permanent armed-observed latch and
request quotas independently guard local nonpersistent stream negotiation.
Device ACK means the Pi queued an intent, **not** that ATTITUDE or position
arrived. Its viewer tokens are restricted to one aircraft and expire after
ten minutes. A dropped connection cannot replay old queued telemetry.

## Relay provisioning (not automatic)

Use Python 3.12, a private Tailscale HTTPS proxy and secret storage.
Do not expose the application directly to the public Internet.

    python3 -m venv .venv-vihanga
    . .venv-vihanga/bin/activate
    python -m pip install -r services/vihanga_link/requirements-cloud.txt

The service requires these variables from a protected private environment:

- VIHANGA_VEHICLE_ID: explicit aircraft ID (example AH-HW-001)
- VIHANGA_FC_SYSID: physically verified flight-controller MAVLink system ID; set the **same** value on relay and Pi
- VIHANGA_ALLOWED_HOSTS: exact private gateway hostname, not a wildcard
- VIHANGA_OPERATOR_NAME: dedicated operator username
- VIHANGA_OPERATOR_PASSWORD_HASH: Argon2id password hash, NOT plaintext
- VIHANGA_JWT_KEY: independent cryptographically random key, 32+ bytes
- VIHANGA_DEVICE_TOKEN_SHA256: hex SHA-256 of random Pi-only device secret
- VIHANGA_ALLOW_DISARMED_STREAM_REQUEST=1: **optional** allow a signed-in operator to request preset disarmed telemetry streams via the private device WSS; default 0 denies all requests

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
- /api/v1/vehicles/{vehicle_id}/telemetry/probe (opt-in, POST, JWT, bounded)
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
    VIHANGA_DISARMED_PARAM_SNAPSHOT=0

Run:

    python -m services.vihanga_link.pi_agent

The local-only, opt-in stream probe uses MAV_CMD_SET_MESSAGE_INTERVAL
when an identified ArduPilot heartbeat proves disarmed. It requests
non-persistent rates: ATTITUDE 30 (5 Hz), GLOBAL_POSITION_INT 33 (2 Hz),
VFR_HUD 74 (2 Hz), SYS_STATUS 1 (1 Hz), GPS_RAW_INT 24 (1 Hz),
BATTERY_STATUS 147 (1 Hz). If no actual ID30 is observed, at most one
additional local probe is attempted after a minimum 15-second interval.
Any observed ID30 or armed heartbeat disables further probes in that
session. It never changes persistent parameters or accepts cloud-originated
flight commands. Enable this feature only for an approved, disarmed bench.

The Pi accepts only the named `disarmed_stream_probe` JSON intent over the
private device WSS; any `COMMAND_LONG`, arming, mission, calibration,
parameter-set or raw GCS MAVLink payload is ignored and never decoded into
an FC write. After explicit operator consent, the PI local UART thread may
issue one preconfigured volatile set of stream-rate requests while disarmed.
At most two remote intents are queued per process, with a 15-second cooldown;
loss of disarmed heartbeat or any observed arming refuses them. No automatic
flight authorization is provided.

The UART owner logs measured 10-second HEARTBEAT (ID 0), ATTITUDE (ID 30),
position, GPS and HUD windows together with the forwarded WSS counts, queue
drops and foreign-system rejections. If the Pi counts HEARTBEAT but no ID 30,
inspect the FC stream rate on the disarmed bench; a cloud or GCS UI update
cannot generate missing physical telemetry. If Pi reports ID 30 but the cloud
relay does not, inspect relay authentication, CRC rejection counts and WSS
backpressure. `/api/v1/mavlink/health` additionally reports genuine 5-second
packet rates, frame/heartbeat/attitude ages, and per-ID counters. Missing
samples return null ages and zero rates, not synthetic values.

This cannot repair wrong wiring, serial baud/protocol, a broken UART
owner, an FC that ignores interval requests, or a missing relay deployment.

## Windows source update and launch

Close all Mission Planner/Vihanga processes before rebuilding:

    cd "$HOME\AeroHelm-GCS"
    git switch master
    git pull --ff-only origin master
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-VihangaVerified.ps1 -Apply

Launch the verified build with the new dedicated private relay origin:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 -NoAutoTcp -LinkOrigin https://YOUR-PRIVATE-RELAY.ts.net

This does not replace Oracle M1 authentication. The PATHS connection
prompts for a separate new relay login when LinkOrigin is configured.
The simplified operator toolbar retains native DATA / PLAN / SETUP /
CONFIG plus PATHS / CAMERA / TOOLS. PATHS has just two connection choices:
Cloud telemetry and Direct USB/radio. Independent cloud ATTITUDE, LINK,
SURVEY and Settings live under TOOLS. Simulation mission previews appear
under TOOLS only when a dedicated relay origin is configured.
Experimental Operations and Fleet/INTEL pages are hidden by default;
they can be inspected in a development profile by setting
VIHANGA_ADVANCED_TOOLS=1 before launching.
Open TOOLS -> Link diagnostics to inspect `message_ids`, live stream ages,
real HB/ATT frames and the last Pi telemetry-request ACK. When configured,
use `Request real FC streams` **only for a physically disarmed, propeller-free
bench**, after explicit operator confirmation. The relay's accepted/queued
response is not evidence of any FC sample; wait for rising real ID30/33/74
counts. Then choose PATHS -> Cloud telemetry to connect the native HUD. Disconnect
a previous USB/radio connection deliberately; automatic link transfer
is disabled.

## Tests and release acceptance

    python -m pip install -r services/vihanga_link/requirements-test.txt
    python -m pytest -q services/vihanga_link

An optional one-time parameter-list query can be enabled with
VIHANGA_DISARMED_PARAM_SNAPSHOT=1 after verifying FC identity and the
disarmed bench. It issues only PARAM_REQUEST_LIST locally over the Pi UART
and forwards the resulting PARAM_VALUE frames; it does not write parameters,
prove cache completeness, or enable remote GCS commands. The Pi also holds
an exclusive lock to prevent two instances starting across an ownership check.

The private relay exposes POST /api/v1/sim/mission/preview to a short-lived
authenticated operator viewer. It validates operator-declared rectangular
geofences, finite coordinates, speed and altitude limits, route length,
declared endurance/reserve and returns a deterministic plan digest,
virtual waypoint timeline and approximate camera capture count. It is
simulation-only: it never sends MAVLink, changes parameters, starts missions
or claims calibrated energy/dynamics prediction.

These are synthetic offline tests, NOT Oracle deployment tests or
physical aircraft acceptance.

Acceptance gates:
1. The sole Pi UART reader counts verified real HB (0) and ATTITUDE (30),
   plus relevant position/GPS/battery IDs, with correct stream rates and
   the configured FC SYSID verified on both Pi and cloud relay.
2. Authenticated Pi WSS forwards real raw frames to the private hub;
   the operator viewer receives the identical bytes and count.
3. Native Windows Mission Planner HUD and attitude monitor follow
   disarmed prop-off vehicle movement and mark interruption stale.
4. Zero replay of stale frames after a drop/reconnect; exclusive UART
   ownership, no auto-radio switchover, and JWT expiration enforced.
5. Windows compiled EXE/DLL and source commit match the release manifest.
6. Demonstrate that unauthenticated probe requests, armed/stale FC state,
   excess quota, foreign system ID and arbitrary raw MAVLink commands are
   rejected without FC actuation. The ACK and a fresh real ID30/33/74 count
   must be checked separately.
7. Arming, mission upload, parameter mutation, actuator commands,
   automatic flight authority and autonomous flight are NOT enabled
   by this diagnostic-duplex development service.

Providing full USB-equivalent COMMAND and AUTONOMY authority requires
separate supervised, audited bidirectional control with onboard failsafe,
geofence, command TTL/idempotency and hardware acceptance. This telemetry
transport alone cannot satisfy that separate milestone.

Do not paste JWTs, private endpoints, passwords or device secrets into
public bug reports.
