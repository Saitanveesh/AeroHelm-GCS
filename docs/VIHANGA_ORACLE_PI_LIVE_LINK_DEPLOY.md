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
sudo apt-get update
sudo apt-get install -y git
```


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

For a **real Oracle-to-Pixhawk disarmed command round trip** (not a
simulator), after the physically supervised setup and a functioning private
relay/Pi connection, enable both opt-ins in their respective protected
systemd EnvironmentFiles:

* Oracle `/etc/vihanga/relay.env`:
  `VIHANGA_ALLOW_DISARMED_STREAM_REQUEST=1`
* Pi `/etc/vihanga/pi.env`:
  `VIHANGA_DISARMED_STREAM_PROBE=1`

Use `sudoedit` to change only those two keys and restart **only** the
matching `vihanga-relay.service` and `vihanga-pi.service`, respectively.
The site operator must re-verify **disarmed, propellers removed** before
restarting; the Pi immediately may issue bounded volatile stream requests
after its own disarmed FC heartbeat. Never turn on an arbitrary command
tunnel or bypass a vehicle's onboard failsafes.

Then on the Oracle VM:

```bash
cd /opt/vihanga/source
/opt/vihanga/venv/bin/python -m services.vihanga_link.deploy.probe_fc_streams \
 --origin https://aerohelm-vcn.tailf3ab6a.ts.net:8443 \
 --vehicle AH-HW-001 --operator operator
```

This prompts for explicit on-site consent and a protected operator password,
asks the cloud to request preset stream rates, and looks for a **new physical
Pixhawk COMMAND_ACK (ID77, command 511)** and genuine rising ATTITUDE
frames. A Pi WebSocket JSON acknowledgment, a `MAV_RESULT=0` acceptance,
and actual ID30/33/74 output are three *different* pieces of evidence.

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


## 7. Debian 13 physical Pi: production dual-direction acceptance

The production directories on **both** computers are `/opt/vihanga/source`
(sparse Git checkout) and `/opt/vihanga/venv` (private Python runtime).
The service unit files are installed into `/etc/systemd/system/`.
The VM uses `/etc/vihanga/relay.env`; the Pi uses
`/etc/vihanga/pi.env`, with the token at
`/var/lib/vihanga/device-token`. Never commit or copy those secrets.

After enabling the physical UART but disabling the Linux serial login/console
with raspi-config, reboot. On the Pi, inspect:

```bash
cat /proc/cmdline
ls -l /dev/serial0 /dev/ttyAMA10
sudo systemctl status serial-getty@ttyAMA10.service --no-pager || true
sudo fuser -v /dev/ttyAMA10 || true
```

**The old password recovery option `systemd.debug_shell=1` must be
removed before production deployment.** With sudo restored, use
`/boot/firmware/cmdline.txt` only if it is confirmed to be the active boot
file; preserve a backup, remove just that one temporary argument, and reboot.
Never run a privileged debugging shell on a connected aircraft.

After installing `vihanga-pi.service` but **before starting it**, execute
the read-only preflight:

```bash
cd /opt/vihanga/source
sudo bash services/vihanga_link/deploy/preflight_pi.sh /dev/ttyAMA10
```

The preflight fails if it sees a UART kernel console, active serial-getty,
any UART owner, the recovery debug shell, or missing `vihanga` dialout
membership. Identify the old service with `fuser` and systemd before
stopping anything. Re-run the check before beginning the transfer.

With both protected configurations prepared, the relay healthy and
`tailscale serve status` confirming **:8443** (without resetting the
existing **:443** mapping), start only the Pi service:

```bash
sudo systemctl enable --now vihanga-pi.service
sudo journalctl -u vihanga-pi.service -n 60 --no-pager
```

On Oracle, obtain the full Compute OCID from OCI instance metadata, not
from the VM display name:

```bash
curl -fsS -H 'Authorization: Bearer Oracle' \
  http://169.254.169.254/opc/v2/instance/ |
  python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])'
```

Use that OCID for the **private ping + authentic operator telemetry** check:

```bash
cd /opt/vihanga/source
sudo /opt/vihanga/venv/bin/python \
  -m services.vihanga_link.deploy.validate_full_duplex \
  --origin https://aerohelm-vcn.tailf3ab6a.ts.net:8443 \
  --vehicle AH-HW-001 \
  --operator operator \
  --oracle-ocid 'REPLACE_WITH_FULL_INSTANCE_OCID'
```

The validator refuses an unverified OCI instance/incorrect HTTPS port,
logs in without printing the password, issues an authenticated
`POST /api/v1/vehicles/{vehicle_id}/link/ping`, verifies the exact Pi
`link_pong` identifier, and receives/CRC-checks original FC MAVLink frames
from the operator WebSocket. It prints each physical message ID count.
A verified Pi ping is **network round trip only**: it does not reach the FC.

Only with a **propellers-removed, disarmed, supervised** FC, enable both
independent flags by editing each protected environment file:

- Oracle `/etc/vihanga/relay.env`:
  `VIHANGA_ALLOW_DISARMED_STREAM_REQUEST=1`
- Pi `/etc/vihanga/pi.env`:
  `VIHANGA_DISARMED_STREAM_PROBE=1`

Then explicitly restart the corresponding services separately, recheck
fresh physical heartbeat, and execute the same validator with
`--bench-fc-probe`. This uses the pre-existing fixed
`MAV_CMD_SET_MESSAGE_INTERVAL` command 511, which ArduPilot treats as a
link-local, volatile stream-rate configuration, **not** a flight command.
The Pi requires a fresh disarmed ArduPilot heartbeat, and refuses if any
armed FC heartbeat was observed in this process lifetime.

This optional test distinguishes **three stages**:

1. Authenticated cloud-to-Pi WebSocket request acknowledged by the Pi.
2. Newly observed, valid physical Pixhawk `COMMAND_ACK` for 511 with
   accepted result; an accepted ACK is not proof of telemetry arrival.
3. Actual new ID30 ATTITUDE and ID33 position or ID74 VFR_HUD after the
   request; missing messages are explicitly reported as a failed acceptance.

This is the safe physical full-duplex *diagnostic* foundation. Cloud-guided
arming, takeoff, mission upload, waypoint dispatch, parameter writes, and
native outgoing MAVLink forwarding remain **disabled** pending separate
authorization, lease/failsafe enforcement, RC override and hardware tests.


## 8. Zero physical frames with a healthy cloud WebSocket — Raspberry Pi 5

If the Pi journal repeatedly reports
`10s physical UART IDs HB=0 ATT=0 POS=0 HUD=0`, while Oracle reports
`device_online=true` and zero frames, **do not** enable cloud flight
commands. This means the Pi agent has not parsed valid Pixhawk MAVLink
frames. A correct systemd service and `fuser` owner prove only that
Linux opened a serial device.

**Pi 5 serial-port caveat:** Official Raspberry Pi documentation
identifies `/dev/ttyAMA10` as the **dedicated debug UART**, exposed
through the small UART debug header; the GPIO14/GPIO15 header UART is
normally `/dev/ttyAMA0` when enabled. `/dev/serial0` may therefore
point to `ttyAMA10` even when the Pixhawk wires run to GPIO14 and
GPIO15. See https://www.raspberrypi.com/documentation/computers/configuration.html

On the Pi, stop only the new Pi agent before any independent serial test:

```bash
sudo systemctl stop vihanga-pi.service
tr -d '\000' </proc/device-tree/model; echo
ls -l /dev/serial0 /dev/ttyAMA0 /dev/ttyAMA10 2>&1
pinctrl get 14 15
sudo fuser -v /dev/ttyAMA0 /dev/ttyAMA10 || true
```

Determine **where the Pixhawk TX/RX/GND wires are physically
connected**, with the vehicle disarmed and propellers removed:
the Pi 5 debug header is not the 40-pin GPIO header. Don't move powered
wires, guess baud, or alter ArduPilot `SERIALx_*` parameters blindly.

Use the optional **receive-only** serial diagnostic after stopping the
sole-owner Pi service:

```bash
cd /opt/vihanga/source
sudo /opt/vihanga/venv/bin/python \
  -m services.vihanga_link.deploy.probe_uart_readonly \
  --device /dev/ttyAMA0 --baud 57600 --seconds 8
```

The script refuses to run while the agent or any competing reader owns
that UART. It never issues `COMMAND_LONG`, sets stream rates, or
writes serial bytes. Use the physical port and serial baud actually
confirmed by its **valid parsed** MAVLink messages; do not interpret
nonzero raw bytes alone as healthy telemetry. Recheck at a verified
alternative baud only if the flight-controller serial settings support
it.

Only if physical wiring and observed MAVLink justify a different port,
edit just `VIHANGA_PI_SERIAL` (and, if necessary,
`VIHANGA_PI_BAUD`) in the already-created
`/etc/vihanga/pi.env` with `sudoedit`, and run the preflight
against that new device before restarting only `vihanga-pi.service`.
Do **not** rerun `configure_pi` or regenerate credentials for a
port correction.

### Repeated private WSS disconnects with zero UART frames

The old relay used `asyncio.wait_for(ws.receive(), timeout=12)`,
unconditionally closing an otherwise authenticated idle device
connection after 12 seconds without MAVLink application frames. The
relay now accepts an idle device WebSocket indefinitely and relies on
WebSocket protocol-level ping/pong and disconnect events to detect a
dead peer. This is deliberately separate from the physical-UART fix.

On Oracle, for a code-only update when the VM has a clean checkout
and the existing relay configuration is already healthy:

```bash
sudo systemctl stop vihanga-relay.service
sudo git -C /opt/vihanga/source pull --ff-only
sudo systemctl start vihanga-relay.service
sudo systemctl status vihanga-relay.service --no-pager
```

Keep Tailscale Serve `:8443` and the legacy `:443` mappings in
place; this fix needs **no token/password rotation** and no change
to the Pi's device token. The device will reconnect automatically.
