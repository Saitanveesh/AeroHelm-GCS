# Vihanga physical telemetry link — parallel Oracle/Pi deployment

**Scope:** This procedure installs the real hardware telemetry transport, not
a simulated aircraft. It preserves the live legacy `aerohelm-api.service` and
Tailscale HTTPS :443. New Vihanga uses a persistent, authenticated bidirectional
**WebSocket** on Tailscale HTTPS :8443, with the relay bound to loopback
127.0.0.1:8765. Tailscale provides the encrypted VPN and TLS proxy. Neither
Oracle public firewall nor an internet-facing UDP/TCP MAVLink listener is needed.

A bidirectional WebSocket transport does **not** by itself mean unrestricted
USB-equivalent physical command authority. This build carries complete genuine
Pixhawk telemetry upward and only a fixed, disarmed stream-rate probe downward.
The native Windows bridge remains receive-only; no arming, takeoff, mission
execution or parameter mutation is enabled on the cloud.

## Preflight before any serial changes

* VM verified: Ubuntu 24.04 LTS, `aerohelm-api` :8000, Tailscale :443,
  hostname `aerohelm-vcn.tailf3ab6a.ts.net`, sufficient disk/memory.
* Raspberry Pi serial device path, baud, MAVLink instance, FC SYSID, ground
  and voltage-level compatibility **must be physically verified**.
* Use a propellers-off, disarmed maintenance setup and an on-site operator.
* The **original** sole Pi UART reader must be identified before stopping it.
  Do not use `pkill python`, kill an unknown process, or have two readers.
* The new device-token digest is generated locally on the Pi. Only the digest
  is copied to Oracle. Do not transmit plaintext secrets or JWTs in chat.

## 1. Install the Python relay on Oracle (do not start yet)

SSH into the VM as `ubuntu`. Run:

```bash
sudo install -d -m 0755 /opt/vihanga
sudo git clone --filter=blob:none --sparse --depth=1 --branch master \
  https://github.com/Saitanveesh/AeroHelm-GCS.git /opt/vihanga/source
sudo git -C /opt/vihanga/source sparse-checkout set services/vihanga_link
cd /opt/vihanga/source
sudo bash services/vihanga_link/deploy/install_oracle.sh
```

This creates `vihanga-relay.service` and a dedicated venv, but does not start
or alter the legacy M1 API. On subsequent revisions, stop the new Vihanga
unit, then use a reviewed `git pull --ff-only` and reinstall; never reset a
dirty checkout to a moving branch or overwrite credentials.

## 2. Prepare Pi, generate a unique random device token

SSH into the Raspberry Pi, then run:

```bash
ls -l /dev/serial/by-id/ /dev/serial0 /dev/ttyAMA0 /dev/ttyUSB0 2>/dev/null || true
sudo systemctl list-units --type=service --all --no-pager |
  grep -Ei 'aerohelm|mavlink|serial|telemetry|python|drone' || true
sudo ss -ltnp | grep -E ':(14550|5760)\b' || true

sudo install -d -m 0755 /opt/vihanga
sudo git clone --filter=blob:none --sparse --depth=1 --branch master \
  https://github.com/Saitanveesh/AeroHelm-GCS.git /opt/vihanga/source
sudo git -C /opt/vihanga/source sparse-checkout set services/vihanga_link
cd /opt/vihanga/source
sudo bash services/vihanga_link/deploy/install_pi.sh
sudo /opt/vihanga/venv/bin/python -m services.vihanga_link.deploy.configure_pi
```

Provide the **real** `/dev/...` UART path and baud, the single vehicle ID,
FC SYSID, hostname `aerohelm-vcn.tailf3ab6a.ts.net`, and the full Oracle
*Compute instance* OCID. The tool writes a root-only `pi.env` and a separate
0600 token readable only by the `vihanga` service account, prints a **SHA-256
digest only**, and does **not** open the UART. Record the digest privately.

Obtain the full OCI instance OCID from the **Oracle VM** with:

```bash
curl -fsS --max-time 5 -H 'Authorization: Bearer Oracle' \
  http://169.254.169.254/opc/v2/instance/ |
python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])'
```

## 3. Configure, start and expose the separate Oracle service

Back on the Oracle VM, enter the Pi device-token **digest** (not token)
and a new independent operator password at the interactive prompts:

```bash
cd /opt/vihanga/source
sudo /opt/vihanga/venv/bin/python -m services.vihanga_link.deploy.configure_relay
sudo systemctl enable --now vihanga-relay.service
sudo systemctl status vihanga-relay.service --no-pager
curl -fsS http://127.0.0.1:8765/health

# Preserve the existing :443 proxy of the old API.
sudo tailscale serve --bg --https=8443 http://127.0.0.1:8765
tailscale serve status
curl -fsS https://aerohelm-vcn.tailf3ab6a.ts.net:8443/health
```

Expected: a verified OCI identity digest from the **new relay** on :8443,
and the unchanged old `/health` service on :443. Never run
`tailscale serve reset`; it would remove the working :443 mapping.
If the command is unavailable on your Tailscale version, stop here and
inspect `tailscale version` / `tailscale serve --help` rather than replacing
:443. If the tailnet policy blocks :8443, change the private ACL policy; do
not expose public OCI ports.

The VM must have `VIHANGA_ORACLE_INSTANCE_OCID` set in the protected relay
environment. The service compares it with its own IMDSv2 metadata and fails
closed if running on a different instance.

## 4. Transfer UART ownership only when the new relay is healthy

On the Raspberry Pi identify the **actual selected serial device**, then:

```bash
sudo fuser -v /dev/REPLACE-WITH-VERIFIED-SERIAL-DEVICE
sudo systemctl list-units --type=service --all --no-pager |
  grep -Ei 'aerohelm|mavlink|serial|telemetry|drone' || true
```

Stop the **verified previous UART-owning service by its actual systemd name**.
Do not guess a service name, stop all Python processes, or stop the existing
Oracle API. The new Pi agent also rejects a serial port already in use.
Before starting it, verify from the Pi:

```bash
curl -fsS https://aerohelm-vcn.tailf3ab6a.ts.net:8443/health
sudo systemctl enable --now vihanga-pi.service
sudo systemctl status vihanga-pi.service --no-pager
sudo journalctl -u vihanga-pi.service -n 60 --no-pager
```

Observe genuine per-ID physical UART counts (0 HEARTBEAT, 30 ATTITUDE,
33 GLOBAL_POSITION_INT, 74 VFR_HUD, 1 SYS_STATUS, 24 GPS_RAW_INT, 147
BATTERY_STATUS, plus 77 COMMAND_ACK after a local stream request).
The new relay's `/api/v1/mavlink/health` distinguishes the Pi's JSON
request ACK from a real Pixhawk `MAV_CMD_SET_MESSAGE_INTERVAL` ACK
(ID77, command=511). An ACK result of 0 means the FC accepted the command;
it does **not** prove the requested telemetry frames subsequently arrived. The Pi verifies both the pinned private HTTPS hostname
and **the exact :8443** Oracle instance identity before transmitting its
bearer token. It does not accept arbitrary GCS MAVLink commands.

## 5. Prove real packets cross both WebSockets

Run on Oracle (or another approved tailnet node):

```bash
cd /opt/vihanga/source
/opt/vihanga/venv/bin/python -m services.vihanga_link.deploy.verify_link \
 --origin https://aerohelm-vcn.tailf3ab6a.ts.net:8443 \
 --vehicle AH-HW-001 --operator operator --seconds 12
```

This interactive read-only verifier sends the operator password only to
the explicitly selected private origin and prints *actual received* MAVLink
message counts. An ID30 count of zero means the native HUD will still be
stale; no synthetic roll/pitch is generated. It is expected that GPS and
position remain unavailable indoors without the necessary fix.

From a **verified Windows GCS checkout rebuilt for the same source revision**:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 `
    -NoAutoTcp `
    -LinkOrigin 'https://aerohelm-vcn.tailf3ab6a.ts.net:8443' `
    -OracleInstanceOcid 'REPLACE-WITH-ACTUAL-FULL-OCI-INSTANCE-OCID'
```

Use **PATHS > Cloud telemetry** to sign in and select the native connection.
Verify DATA/Quick and actual ID30/33/74 packet rates, disconnection stale
state, and the independently selected USB/radio backup.

## 6. Recover real ID30 if HEARTBEAT arrives but attitude does not

ArduPilot 4.0+ allows a device on the *correct* MAVLink serial instance to
request **volatile** stream intervals via `MAV_CMD_SET_MESSAGE_INTERVAL`.
On a propellers-off, identified disarmed bench, the Pi's existing local
`request_streams_once` routine can request ID30 at 5 Hz, ID33 and ID74
at 2 Hz, and SYS_STATUS/GPS/BATTERY at 1 Hz. The initial installer leaves
`VIHANGA_DISARMED_STREAM_PROBE=0`.

After the onboard operator confirms the port, firmware, SYSID and disarmed
status, set `VIHANGA_DISARMED_STREAM_PROBE=1` in the protected
`/etc/vihanga/pi.env`, restart **only** `vihanga-pi.service` and recheck
the observed counters. This does not enable arm, takeoff or mission upload.

If ID30 remains zero, inspect the FC's actual SERIALx/MAVLink stream
configuration. `SRx_EXTRA1` / `MAVx_EXTRA1` group names depend on firmware
and MAVLink channel mapping; do not blindly modify `SERIALx_PROTOCOL`,
`SERIALx_BAUD`, GPS, RC or failsafe parameters.

## Remote GUIDED/takeoff limitation

The new persistent WebSocket can exchange authenticated messages in both
directions. Today its **only supported downstream physical operation** is
a fixed, disarmed telemetry-stream request. It cannot carry a raw
`COMMAND_LONG` for GUIDED, arm, takeoff or waypoint execution, and the
Windows native adapter still discards outbound MAVLink. Running a generic
Oracle `pymavlink` takeoff script against `127.0.0.1:8765` would therefore
**not** command the aircraft and must not be represented as working.

The physical command phase must add an independently authenticated and
supervised command channel, per-aircraft leases, command-level allowlists,
bounded TTL, monotonic sequence, onboard/RC override, explicit FC ACK and
mode/parameter/mission readback, plus live SITL/HIL and propellers-off tests.
Armed takeoff commands are not enabled by this deployment. Use the real
telemetry and disarmed stream negotiation to establish the first physical
round trip before authorizing motor actuation.
