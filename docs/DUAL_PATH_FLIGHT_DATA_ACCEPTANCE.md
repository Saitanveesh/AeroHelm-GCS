# AeroHelm GCS 0.3 dual-link acceptance — cloud observation + pilot backup

**Build stage:** staged on `feat/gcs-research-cybershield-twin-ui`.
**Control authority:** Mission Planner native physical USB/telemetry radio only,
under the authorized pilot. Cloud MAVLink is strictly receive-only.

## Operator connection UX

Click **FLIGHT** in the nine-tile touch console, or the dedicated
**PATHS** toolbar symbol:

| Path | Native action | What it is NOT |
|---|---|---|
| CLOUD VIEW | Authenticated Oracle WSS → local 127.0.0.1:5760 → native Mission Planner Flight Data | Not a vehicle command/arming/parameter-upload channel |
| CLOUD ATTITUDE | Separate JWT-authenticated roll/pitch preview from actual observed MAVLink ATTITUDE ID 30 | Not a substitute heartbeat, synthetic horizon or certified flight instrument |
| PILOT BACKUP | Shows native Mission Planner USB/telemetry-radio COM/baud selection | Not cloud failover, not auto-connecting, not proof of redundant radio hardware |

The PATHS selection opens **no public ports** and does not send a
flight-controller command. If the cloud bridge failed its original
bind, choosing CLOUD VIEW retries the same localhost-only listener
after the port is free. It never kills unidentified processes.

## Critical physical architecture

The Pi 5 remains the **sole** reader of its dedicated flight-controller
UART (`/dev/ttyAMA0`), carrying receive-only AeroHelm cloud telemetry
via the FC's existing MAVLink stream. Never have a second Pi service
open this UART just to fix the HUD.

**Pilot backup requires a physically independent flight-controller link**:
an appropriate separately configured telemetry-radio connection or
direct USB to the Windows pilot computer. This may require a distinct
FC telemetry connector, cable, compatible radio pair, separate supply
and a local FC serial-port configuration/baud check. The UI cannot
create such redundancy through software.

Do not connect two independent masters to the **same** UART wires.
Verify FC baud, MAVLink version, radio link quality, physical range,
antenna placement, serial resource use, power integrity, telemetry
port assignment and applicable operating rules before calling the
radio backup ready.

## Flight Data acceptance gates (FC disarmed, ALL PROPS REMOVED)

1. Start only the reviewed rebuilt GCS. Confirm SETTINGS displays the
   compiled plugin fingerprint, and that all nine modules, toolbar icons
   and PATHS open without exceptions.
2. Inspect LINK: Oracle must report fresh *actual* HEARTBEAT ID 0
   and ATTITUDE ID 30, not merely increasing generic `frames_received`.
   `viewers=0` means no native cloud observer currently attached.
3. Select **CLOUD ATTITUDE**: the independent real-only preview must
   show changing roll and pitch when the operator carefully tilts the
   powered, disarmed Pixhawk with props removed. When source goes stale,
   angles must disappear; no synthetic tilt animation is permitted.
4. Select **CLOUD VIEW**: confirm local port 5760 listener binds,
   Oracle viewer count increases, Mission Planner obtains a genuine
   heartbeat, and the native horizon follows the same tilt direction.
   If HEARTBEAT is absent, the native HUD must reject connection.
5. Disconnect cloud/Tailscale at a safe bench state; check status
   becomes stale, no old angles appear live and no vehicle command
   reaches the Pi.
6. Deliberately disconnect the native Mission Planner cloud viewer.
   Choose **PILOT BACKUP** and connect the **already configured**
   independent physical radio or direct USB using native COM/baud.
   Verify actual FC heartbeat, mode, telemetry and command authority
   under on-site pilot supervision. There is **no automatic switch**.
7. Check simultaneous Pi cloud observation and separate radio telemetry
   only after confirming isolated serial connections and sufficient
   bandwidth; verify that neither link starves the FC or changes
   required heartbeat/attitude rates.
8. Confirm a safety-critical aircraft failsafe is implemented and
   tested at the flight-controller/RC layer, not delegated to
   Oracle, Tailscale, Windows, Kerberos or AeroHelm.

## If cloud HUD does not move

Use the updated Oracle `/api/v1/mavlink/health` response:

- Generic frames fresh, HEARTBEAT missing: inspect FC-provided
  telemetry stream and selected FC serial interface.
- HEARTBEAT present, ATTITUDE missing: investigate FC MAVLink stream
  rate/telemetry port configuration at a supervised **local bench**
  session. Cloud read-only viewers do not send stream-rate requests.
- Both frames fresh, `viewers=0`: native Windows client has not
  authenticated/attached to the cloud WebSocket; inspect local
  bridge and port 5760.
- Both frames fresh, viewers present, native HUD frozen: inspect native
  bridge per-type counters, GCS TCP connection and actual FC attitude.
  Do not fabricate messages or silently bypass the native heartbeat gate.

The real-only cloud attitude preview can make genuine roll/pitch data
visible even while the native Mission Planner connection still lacks
a proper HEARTBEAT; it is diagnostic, **not flight authorization**.

## Windows launch choices

Full cloud-integrated UI, using the verified release updater:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git status --short
git pull --ff-only origin feat/gcs-research-cybershield-twin-ui
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-AeroHelmLatest.ps1 -Apply -Launch
```

If a verified old Python bridge owns TCP 5760, close its terminal
normally or use explicit `-StopVerifiedLegacyBridge`. Never kill
an unknown PID, open 5760 on external interfaces or disable the firewall.

Offline physical pilot radio backup — **separate, deliberate entry point**
that does not depend on Oracle/KDC login:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-DirectBackup.ps1 -AcknowledgePilotAuthority
```

Run backup only if you are the authorized pilot at the aircraft and
understand that native direct COM may issue **real** MAVLink commands.
It is intentionally not a remote cloud fallback and requires the
existing verified EXE/DLL release manifest.

## Cloud identity

Default login remains private Tailscale HTTPS with operator JWT.
Kerberos/Negotiate is optional, **disabled by default**, and requires
the separately verified private KDC, SPNEGO gateway, keytab, DNS, TLS
certificate, principal mapping and anti-replay exchange. A Kerberos
login can never elevate the cloud into a physical MAVLink command link.
