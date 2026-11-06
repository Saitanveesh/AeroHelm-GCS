# AeroHelm Multipath GCS — cloud observation + independent pilot backup

## Release boundaries

**Source milestone**, not physical flight qualification. No remote actuation or
automatic switch of native COM/TCP, RC, motors, missions or aircraft modes.
A separate telemetry radio is **not** an RC transmitter/failsafe substitute.

### Connection architecture

```text
Pixhawk TELEM1 --one UART reader--> Raspberry Pi 5
        |                           |
        |                      WSS/MAVLink (CRC checked)
        |                           |
        |                  Private Oracle M1 gateway
        |                           |
        |                   Tailscale private WSS
        |                           |
        |                   AeroHelm cloud bridge
        |                        127.0.0.1:5760
        |                           |
        |                  Native Mission Planner HUD
        |
        +--- SEPARATE FC TELEM2 or USB port, separately configured ----+
                                                                  |
                                                     Independent pilot-side
                                                     radio receiver / USB
                                                                  |
                                                     Windows COMx Mission
                                                     Planner native transport
```

The Pi alone owns `/dev/ttyAMA0` and TELEM1, and cloud/GCS do **not**
open a second Pixhawk UART. The pilot radio must be physically available,
configured and bench-tested on a **separate FC telemetry channel** or local
USB port, including independent power, antenna, airframe wiring and
firmware-specific serial settings. Only an on-site authorized pilot connects
directly. A direct Mission Planner link may send normal MAVLink parameter
requests and other commands; it must not be confused with the cloud viewer.

## Windows operator experience

- `PATHS` toolbar always exists. It offers CLOUD VIEW, PILOT DIRECT
  USB/radio, and an independent real-only CLOUD ATTITUDE preview.
- `PILOT DIRECT` opens a dedicated form that enumerates real Windows COM
  ports, offers explicit baud selection, validates the port again before
  use, and requires a deliberate confirmation before invoking native
  Mission Planner serial connection.
- `CLOUD VIEW` is **strictly receive-only**. Its local TCP 5760 bridge
  discards all outbound GCS bytes and accepts only genuine Pi-observed
  MAVLink frames; no fake HEARTBEAT/ATTITUDE.
- An always-running cloud watchdog updates a nonmodal `PATHS !`
  warning while the main GCS remains open. It measures cloud server
  `device_online`, `fresh`, real HB and ATTITUDE status every five seconds
  over the authenticated read-only API. It does not infer direct radio
  link health from Oracle, or substitute generic frames for verified HUD
  data. It never automatically connects/disconnects anything.
- When Oracle and the KDC both disappear, the **offline pilot backup**
  launch mode retains Mission Planner's local COM telemetry:
  `scripts/Start-AeroHelm-DirectBackup.ps1 -AcknowledgePilotAuthority`.
  The script does not contact Oracle, install a Python bridge, or require
  Kerberos/password cloud login.

## Actual frozen HUD root cause

The user's screenshot showed Oracle `fresh=true` but GCS viewers=0, plus
Mission Planner `No Heartbeat Packets Received`. A connected Pi does not
guarantee that the FC is emitting MAVLink ID 0 (HEARTBEAT) or ID 30
(ATTITUDE) on TELEM1 at a sufficient rate.

The updated M1 backend separately reports true HB and ATT counters/ages.
The operator-only `/api/v1/mavlink/attitude` preview renders fresh,
finite decoded roll/pitch independently from the Mission Planner HUD.
No software layer may fabricate a valid MAVLink heartbeat to make the
native horizon appear operational.

A real hardware bench acceptance must confirm:
1. FC disarmed, props removed, stable Pi and independently powered radio.
2. Oracle M1 deployed version returns heartbeat and attitude counts that
   **increase** in successive six-second measurements.
3. GCS PATHS/CLOUD VIEW has a loopback listener on 127.0.0.1:5760 with
   no old Python bridge process; LINK indicates a GCS viewer attached.
4. Native Mission Planner connects with actual HEARTBEAT and displays
   real changing ATTITUDE as the FC is gently tilted.
5. Direct pilot COM link is independently physically verified on a
   separate FC port, with correct baud and FC stream configuration.
6. Disconnect cloud. PATHS warns, but native/direct connection is **not**
   automatically switched; pilot intentionally chooses backup.
7. Cloud recovery does not silently seize native COM or override the
   onboard failsafe / RC policy. The pilot remains responsible for flight.

Until 2–5 pass, mark **NATIVE HUD UNVERIFIED**.

## Cloud hardening and dynamic roadmap

- **Current implementation:** private Tailscale HTTPS/WSS, JWT expiry,
  local receive-only bridge, 5-second path watchdog, explicit link
  selector, independent pilot COM, backend heartbeat/attitude truth,
  stale-data suppression and isolated V3 research capability flags.
- **Phase A — Observability:** actual per-message timestamp/loss/jitter
  histograms; alerts only (never auto-control); boot-level diagnostics;
  operator identity and SSO expiry status on separate UI lines.
- **Phase B — Resilience:** bounded edge telemetry store-and-forward for
  historical analytics only, signed origin/time receipts, adaptive
  dashboard refresh budgets, multi-region OBSERVATION replication with
  no command authority.
- **Phase C — Security:** KDC/realm provisioned and staged; SPNEGO over
  the same private HTTPS origin; strict Kerberos principal-to-operator
  mapping and one-use signed assertions; service key rotation,
  distributed replay cache and internal mTLS before production SSO.
  Kerberos belongs on the **login/control plane**—not on every MAVLink
  frame, which would add latency and complexity.
- **Phase D — Research:** calibrate the opt-in V3 digital shadow/6-DOF
  physics lab from independently measured mass, inertias, thrust curves,
  wind and logged sensor delay. Validate on held-out data. Never send
  model predictions as live HUD telemetry.
- **Phase E — Operations:** role-controlled fleet scheduling proposals,
  evidence integrity, cameras processed at edge, and explicit supervised
  acceptance checklists, still separated from vehicle actuation.

## Windows deployment

After updating to the reviewed `feat/gcs-research-cybershield-twin-ui`
branch, run the backed-up native Windows build and exact SHA-256 verification:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git status --short
git pull --ff-only origin feat/gcs-research-cybershield-twin-ui
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-AeroHelmLatest.ps1 -Apply -Launch
```

Stop if Git reports unexpected local changes or a merge conflict. Close any
old Python bridge on 5760, or run `-StopVerifiedLegacyBridge` explicitly
only when that PID has been identified as the local AeroHelm script.

**No new public ports are required**: 5760 is Windows loopback only;
the private Tailscale HTTPS address continues to carry cloud traffic.

For **cloud-down local pilot recovery** (separate, offline native GCS
session, only an authorized on-site pilot):
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-DirectBackup.ps1 -AcknowledgePilotAuthority
```

Kerberos staging is documented in the backend branch at
`docs/KERBEROS_STAGING_END_TO_END.md`; it is not enabled by the GCS
update and is intentionally optional.
