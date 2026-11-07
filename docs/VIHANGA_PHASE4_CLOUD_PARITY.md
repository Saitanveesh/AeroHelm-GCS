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

### B — Read-only configuration parity (not implemented)

- Param list/read and mission download must use a separately authorized
  uplink policy, not ordinary telemetry WSS. The Pi retains sole UART
  ownership and batches requests within link bandwidth limits.
- Record parameter completeness, generation timestamp, FC identity, CRC,
  and transport epoch. Never treat a partial cache as a full parameter set.
- Reject any mutation to arming, flight modes, missions or parameters.
- Acceptance: exact readback matches a direct USB reference, without
  changing live FC parameters, and can recover from interruption.

### C — Supervised command and mission authority (not implemented)

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

### D — Cloud-assisted autonomy (not implemented)

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
