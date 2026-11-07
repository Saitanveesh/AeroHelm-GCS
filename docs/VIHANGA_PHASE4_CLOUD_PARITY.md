# Vihanga Phase 4 — cable-equivalent cloud operations and assisted autonomy

## Architectural decision

The native USB Mission Planner control path and a remote cloud path are not
equivalent simply because both carry packets. A safe cloud platform must
separately prove telemetry freshness, command identity/authority, accepted
vehicle state, link-loss behavior, and deterministic onboard execution.

The present production M1 is a legacy **receive-only** deployment. Its
operator JWT, Pi UART agent and cloud WSS relay are not modified by the
development source under services/vihanga_link.

## Four independent capabilities and release gates

### A — Full flight-data mirroring (source implementation in this branch)

- Sole Pi UART owner parses real MAVLink1/2, forwards complete original frames.
- Local disarmed stream negotiation for attitude, GPS, position, VFR_HUD,
  system status and battery data; no persistent FC parameter writes.
- Private WSS device-to-cloud fanout with short-lived, scoped operator JWT,
  per-message counts, freshness, bounded queues and no stale replay.
- Windows PATHS dedicated origin/login and direct WSS-to-native Mission Planner
  receive-only bridge. Independent USB/radio pilot backup remains intact.
- Acceptance: moving real HUD/map and message ID30 at every hop, disarmed
  physical bench validation, exact source/binary hashes, dropout test.
- **Status: source built; deployment, Python CI, Windows MSBuild and real
  hardware acceptance NOT verified.**

### B — Read-only configuration parity (partial source implementation)

- The Pi can send an explicitly opted-in, disarmed-only PARAM_REQUEST_LIST
  locally and forward genuine PARAM_VALUE (ID 22). It does not persist
  settings or permit arbitrary GCS-to-FC commands.
- The private relay checks CRC, source component, parameter count/index,
  per-session completeness and oldest sample freshness (5-minute maximum).
  The signed-in Intel tab reads a complete snapshot, and compares REAL32
  fields without assigning unsafe meanings to other parameter encodings.
- Incomplete snapshots return a non-success status rather than fabricated
  values. New Pi sessions clear previous parameter evidence.
- **Still pending:** native Mission Planner full parameter parity, parameter
  re-request of missing indices, deterministic snapshot provenance across
  FC reboot, mission download and direct USB comparison on hardware.

### B1 — Disarmed telemetry-request duplex (source implementation; not deployed)

- Private relay implements an authenticated POST for a **fixed telemetry-stream
  request only**, gated by a separate relay feature flag, fresh physical
  HEARTBEAT, per-session quota and 15-second cooldown.
- The authenticated Pi device WebSocket receives the fixed intent and returns
  a correlated ACK. The Pi independently requires its local disarmed-only
  stream-probe opt-in, a fresh ArduPilot SYSID/COMPID heartbeat and a permanent
  armed-observed deny latch, then requests only preset volatile FC stream rates.
- ACK means an intent was queued on the device link; genuine rising FC
  message 30 (attitude), 33 (position), 74 (VFR HUD), 1 (system status) and
  24 (GPS) must be observed separately. No synthetic Quick/HUD output.
- The native LINK diagnostics page shows real Quick-feed completeness and
  offers an operator-confirmed bench request only for the explicitly selected
  independent private relay. The legacy Oracle M1 cannot use this endpoint.
- This is **not** a transparent MAVLink TCP duplex, param-write path or flight
  command channel. Those remain subject to the independent Phase C gates.
- Acceptance: compile and hardware bench validation on a propeller-free,
  identified disarmed FC, request accepted/refused/ACK and actual native
  Mission Planner DATA/Quick movement on verified fresh telemetry.

### C — Supervised command and mission authority (physical: not implemented)

- Separate, mutually authenticated command plane. Independent role-scoped
  operator authentication, explicit mission approval, short-lived
  per-aircraft exclusive lease, command sequencing, expiry and ACK.
- Onboard safety arbiter to validate firmware/mode, GPS/EKF,
  geofence, mission intent, battery margin, link quality, independent
  RC override, explicit operator stop and GCS-loss failsafe.
- Command interface must have an explicit simulator-only configuration
  and a separately reviewed physical flight configuration; no generic
  copy-all-MAVLink control tunnel.
- Actuation ACK is distinct from transport ACK. Expired commands never
  replay after link reconnection. Loss of cloud autonomy must defer to
  onboard FC failsafes, not continue issuing cloud commands.
- Acceptance: SITL/HIL, deliberate fault injection, emergency stop,
  independent pilot radio handover and documented hardware risk review.

### D — Cloud-assisted autonomy (simulation foundation implemented)

- A deterministic mission preview API now validates an operator-declared
  geofence, position/altitude/speed limits, endurance reserve and camera
  interval; it returns a revision digest and simulated timing only.
- A separate SQLite-backed offline simulation workflow implements draft,
  independent approval, expiring exclusive virtual leases, ordered virtual
  steps, abort and an integrity-checkable local audit chain. It is NOT
  reachable from physical MAVLink or exposed as a hardware dispatch API.
- Cloud planning and perception are advisory until independently reviewed.
  Use immutable mission revision hashes, replay-safe requests, bounded
  flight envelopes and approved operators before any onboard execution.
- Keep stabilization and immediate collision/landing safety on the vehicle,
  not dependent on cloud round-trip. Cloud compute supports optimization,
  vision inference and fleet scheduling but cannot replace onboard failsafes.
- Separate camera/video plane from MAVLink, with bounded latency, retention
  policy and identity. Verify camera availability independently from FC link.
- Acceptance: simulation campaigns, scenario coverage, private security
  assessment, operator training and physical authority signoff.

## Why direct USB currently works but cloud HUD does not

Observed on Windows before Phase 4: an authenticated cloud WSS received
12 HEARTBEAT messages in 14 MAVLink frames and 0 ATTITUDE messages.
Native USB Mission Planner received much richer data. A connected TCP socket
does not prove real attitude. Old M1 filters/forwards only whatever the Pi
sends; the receive-only GCS cannot ask FC to send missing streams.

The new candidate Pi UART owner can request missing rates locally once
disarmed; however no code edit can verify the actual Pi TELEM port or
change the currently deployed Oracle image without access to those systems.
Do not declare this fixed until both show rising ATTITUDE ID30.

## Deployment invariants

- No forced deletion or silent takeover of the existing Pi UART owner.
- No public unauthenticated MAVLink port, no wildcard cloud credentials.
- No auto pilot-radio failover and no backdoor around native Windows release
  manifest verification.
- The current read-only path cannot arm, navigate, calibrate, or upload
  missions; CLI or UI wording must not imply it can.
- Physical flight is not authorized by code/tests alone.
