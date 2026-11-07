# AeroHelm GCS — cloud link quality and HUD evidence (development candidate)

**Branch:** `feat/aerohelm-cloud-link-quality-v1` (stacked on
`feat/gcs-research-cybershield-twin-ui`). No new cloud command authority.
**Deployment:** NOT deployed to Windows, Oracle, Raspberry Pi or an aircraft by
this source change. Run native Windows and propeller-off acceptance first.

## Problem addressed

A TCP listener, a successful WSS handshake, or an increasing generic frame
counter cannot establish that a native Mission Planner HUD has received a
valid HEARTBEAT (MAVLink message ID 0) and ATTITUDE (ID 30).

Earlier diagnostics used UTC timestamps for freshness. An NTP correction can
make those ages inaccurate. A new cloud connection could also briefly inherit
recent-looking timestamps from a previous WebSocket.

## Implementation

- The native receiver remains bound to **127.0.0.1:5760** and is **strictly
  receive-only**. It discards every byte sent from Mission Planner toward the
  cloud. Normal direct USB/telemetry-radio functionality is unchanged.
- Before counting or forwarding HEARTBEAT and ATTITUDE as cloud HUD evidence,
  independently validate the MAVLink v1/v2 X.25 checksum with their declared
  CRC extras (HEARTBEAT=50; ATTITUDE=39). Reject short HEARTBEAT/ATTITUDE
  payloads and corrupted checksums. Other MAVLink messages remain subject to
  native Mission Planner's protocol parser and the Pi's upstream validation.
- Local quality samples use `Stopwatch.GetTimestamp()`, not wall-clock time.
  The HOME headline requires **both** a currently open cloud WebSocket and
  HEARTBEAT/ATTITUDE observed within five monotonic seconds.
- The LINK/HOME diagnostics tooltip reports rolling five-second observed
  HEARTBEAT and ATTITUDE rates, cumulative attitude gaps over 2.5 seconds,
  invalid HB/ATT checksum counts, and monotonic age of last delivered frame.
- Each new cloud WebSocket session clears its freshness evidence and rolling
  quality state. Ending the WebSocket immediately disables the cloud-live
  headline; lingering counters cannot imply that the link is still live.
- No automatic cloud-to-radio fallback. The pilot must explicitly disconnect
  the cloud viewer and choose independent physical COM/radio backup. RC and
  autopilot failsafes remain onboard responsibilities.

**Interpretation limits:** CRC validates error detection, **not source
authenticity**; session identity rests on the authenticated private cloud
path and verified Pi collection. A local receive rate is **not** end-to-end
latency, one-way transit time, RF signal quality, aircraft safety,
airworthiness, or redundant-link readiness. A valid observation also does not
authorize a cloud command.

## No-hardware test command

From the checked-out **feature branch**:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git status --short
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Run-AeroHelmOfflineChecks.ps1
```

This runs the actual production bridge code through the .NET 8 test harness,
including checksum corruption, empty synthetic HEARTBEAT rejection, MAVLink
v1/v2 ATTITUDE decoding, monotonic staleness, gap counts and session reset.
The Windows build must also compile the integrated plugin and Mission Planner.
The workflow `.github/workflows/aerohelm-cloud-plugin.yml` is configured to
run those contracts on this branch and on PRs. Check actual Actions results;
a configured workflow is not evidence of a passing run.

## Required operator acceptance (ground test only)

1. On a **disarmed** flight controller with **all propellers removed**,
   confirm that Oracle is deployed with genuine HB/ATT counters. If it is
   still the legacy image, perform a separately approved Oracle upgrade
   first. Do not fabricate heartbeat or change FC parameters remotely.
2. Build the exact new commit on Windows and verify EXE/plugin SHA-256 against
   its current release manifest before replacing the known-good runtime.
3. Open PATHS → CLOUD VIEW. Verify WSS viewer attachment, received valid
   HEARTBEAT, current attitude and native Mission Planner artificial horizon.
   Missing heartbeat must remain a failure, not a generated substitute.
4. With the controller safely restrained on a bench, deliberately change
   attitude and compare native HUD with the independent real-only preview.
5. Disconnect the private cloud network; cloud-live must become false
   immediately, attitude must be stale and no outbound commands may reach
   Oracle or Pi. Reconnect requires a new, valid session.
6. Test an **independently wired** FC USB/TELEM radio under pilot supervision.
   The local pilot selects the COM port and confirms the connection manually.
   No automatic transfer of authority is permitted.
7. Confirm operator SSO/Kerberos remains off until its KDC, SPNEGO gateway,
   keytab protection, assertion replay control and principal-role mapping
   have passed independent end-to-end tests.

**Exit criteria:** source tests + full Windows build + UI test + real
controller ground acceptance. Until all pass, the new branch is a development
candidate and not flight-ready software.
