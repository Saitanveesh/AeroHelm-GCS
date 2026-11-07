# Vihanga — Phase 4: bench acceptance and deployed-service contracts

**Source branch:** `feat/vihanga-phase3-native-toolbar`.
**Status:** source changes are not Windows-validated until rebuilt and checked
with the powered Pi/FC. No physical flight clearance is implied.

## Observed November 7 evidence

The operator reported M1 private cloud reachable, a hardware Pi attached,
fresh MAVLink frames, a native receive-only GCS connection and current
heartbeat (~1 Hz). The native bridge counted **740 HEARTBEAT, 0 ATTITUDE**.
M1's authenticated `/api/v1/mavlink/attitude` route returned **404**;
M1 also lacks the GCS's V2/V3 research/security endpoints.

These are separate issues. Missing ATTITUDE *frames* cannot be fixed by
changing an HTTP status label, a stylesheet or cloud-side simulated dynamics.
The GCS must never display fabricated roll/pitch, or treat an M1 total-frame
counter as proof of valid HUD attitude. The native Mission Planner HUD can
display native values for other telemetry types without proving ATTITUDE.

## Changes in the current Windows source

- FLEET/SECURITY/ANALYTICS action area responds to resized windows; two-column
  security evidence wraps, and wider fleet/history tables keep horizontal
  scrolling rather than squeezing values into unreadable columns.
- OPS, INTEL and SETTINGS use a shared WinForms width reflow; LINK wraps
  buttons and exposes a specific **real HB but no ATTITUDE** status.
- FLEET and INTEL request V2 first, then the same-origin R2 fleet view, then
  the existing M1 registry; only 404 triggers fallback. Authentication errors
  are not bypassed. Profile labels identify M1 registry vs R2 observations.
- Cloud capability inspection tries V3 then same-origin R2 and finally the
  deployed M1 read-only MAVLink health endpoint. M1 metrics fallback returns
  only the number of registered aircraft; it does **not** manufacture risk,
  queued mission, server analytics, or live-attitude numbers.
- If M1 lacks a security posture route, the SECURITY page explicitly marks
  server policy/ACL attestation **NOT VERIFIED**. Kerberos remains unprovisioned
  unless a real KDC, SPNEGO gateway, identity mapping and audited denial
  policy are independently deployed.

A 404 on an undeployed V3 service is **NOT** repaired by a GCS update. R2
OIDC and M1 operator-JWT tokens are different identity contracts. An R2
database/API upgrade must be deployed and tested independently; do not send
an M1 JWT to another hostname or disable authentication to make a button work.

## Powered-on Pi/FC — three measured telemetry boundaries

Keep propellers removed, disarmed, and the independent radio/pilot available
for fail-safe evaluation. Do not start a second process that opens the Pi's
UART: the running Pi agent must remain the sole serial-port owner.

1. **FC → Pi agent.** In the *existing* UART-owning agent, inspect observed
   MAVLink message **IDs 0 (HEARTBEAT) and 30 (ATTITUDE)** from the real FC,
   with MAVLink CRC integrity and timestamps. Count both over a fresh 10–20 s
   window. Do not assume 66,400 total frames means any ATTITUDE frames exist.
2. **Pi agent → Oracle M1 ingress.** Inspect the existing Pi sender's
   per-ID forwarding counters, its drop/backpressure queue, and which
   exact IDs the M1 receiver accepts. An ID 30 count that stops here is
   an agent/relay filtering or backpressure problem; preserve monotonic
   timestamps and reject malformed frames.
3. **Oracle M1 WSS → Windows receive-only bridge.** Compare per-ID counters
   on M1's authenticated *hardware-only* WebSocket feed against LINK's
   local receive-only counters. If ingress has ID 30 but LINK still shows
   ATT=0, inspect M1's viewer broadcast selection and WebSocket packet
   boundary handling. Do not synthesize frames to make the HUD appear live.

Record the 10–20 second HB and ATT counts at all three locations, the exact
vehicle ID, source timestamps and server/GCS clock offsets. Only change FC
stream interval or other MAVLink configuration through a deliberate on-site
pilot maintenance procedure; **do not send a stream request from the
read-only cloud viewer**.

A known limitation of this repository is that it does not contain the
**currently running M1 backend or the UART-owning Pi agent source**. Therefore
GitHub commits here cannot change those deployed processes, install the
missing M1 `/api/v1/mavlink/attitude` route, or provision Kerberos.

## Windows acceptance after updated native source is built

Before any next rebuild, save the last-good release backup and close all
Mission Planner processes. From the existing reviewed Windows checkout:

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-AeroHelmLatest.ps1 -Apply
if ($LASTEXITCODE -ne 0) { throw 'Stop: Windows build did not pass.' }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Verify
if ($LASTEXITCODE -ne 0) { throw 'Stop: runtime manifest did not pass.' }
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 -NoAutoTcp
~~~

Inspect normal Windows resize, reduced screen width, 125–200% DPI, and
actual keyboard/touch hit targets; confirm no evidence text is inaccessible.
Open CLOUD, FLEET, SECURITY, INTEL, OPS and LINK. In M1 the capability,
registry and basic metrics views should succeed with explicit limitations;
V2/V3-only functions must remain clearly unsupported. Confirm that a
disconnected Pi makes all freshness indicators fail closed. Native HUD
acceptance requires fresh real ID 0 and ID 30 with plausible observed
roll/pitch, and the documented on-site direct-radio fallback needs separate
authorized, propeller-off testing.

**The desktop Mission Planner HUD is still an upstream WinForms component.**
These changes make the custom Vihanga panels resize and scroll; they do not
convert Mission Planner into a phone-first web application. A separate
accessible responsive mobile client is a distinct deliverable, not a
property of these desktop UI changes.

## R2 separate engineering track

`feat/cloud-oci-private-edge-r2` contains a lightweight Pi observation
bridge, durable PostgreSQL fleet state and isolated fleet analytics worker.
New optional `roll_deg`, `pitch_deg`, `attitude_age_ms` fields are
all-or-none, validated and expire after a combined 2.5-second budget.
Backend GET and cloud worker outputs retain
`DEVICE_REPORTED_UNVERIFIED` provenance and `flight_authorized=false`.
They do **not** replace M1's native WSS MAVLink transport; the Pi producer
integration, OIDC and deployment acceptance remain open.

Do not combine the M1 and R2 security or vehicle-command paths merely to
expose inactive buttons.
