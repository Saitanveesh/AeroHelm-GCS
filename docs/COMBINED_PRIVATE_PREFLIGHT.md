# AeroHelm one-pass private cloud / Pi preflight

**Windows-only development acceptance.** Run this after Windows native
compilation, authenticated cloud deployment and disarmed, propeller-free
Pixhawk/Pi bench preparation. The script does not start or configure Pi,
Oracle, Mission Planner or video services.

## Offline preparation

Use the current `feat/aerohelm-touch-console` development checkout.
First run `scripts/Inspect-AeroHelmWindowsStage.ps1 -Stage Source`, then
the native Windows build and manual TOUCH/FLIGHT/PLAN integration checks.

## Observation-only cloud preflight

Power the bench equipment only with the aircraft **disarmed and all
propellers removed**, using the previously reviewed M2 backend and Pi code.
Keep the backend private under Tailscale, one hardware identity and one
agent UART owner.

For health/status before a confirmed live link (will not claim flight
readiness):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmPrivatePreflight.ps1 -CloudUrl https://YOUR-PRIVATE-HOST.ts.net
```

Once telemetry and qualified parameters are expected on the disarmed bench:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmPrivatePreflight.ps1 -CloudUrl https://YOUR-PRIVATE-HOST.ts.net -VehicleId AH-HW-001 -RequireConnectedPi -RequireCompleteParameters
```

The script prompts for existing operator credentials. **Do not** paste
passwords or bearer tokens into script arguments, terminal history, GitHub,
or chat. It uses TLS certificate verification (no insecure opt-out). The
only POST is operator authentication; all observation routes are GET.

What it checks:
- `/health` is healthy and `hardware-telemetry-only` (not simulator)
- SECURITY posture says physical mission actuation and arbitrary MAVLink
  forwarding are disabled, and the current in-memory audit HMAC chain verifies
- FLEET is read-only, registered aircraft are observed, and MAVLink health
  says fresh, matching the selected aircraft
- Read-only parameter snapshot is `COMPLETE`, `verified_complete`, has
  a positive consistent count and was captured within five minutes
- No missing/old telemetry can silently pass the optional strict bench gates

A JSON observation report is written to ignored
`build-logs/aerohelm-private-preflight.json`. It includes check result
booleans, a vehicle count and GCS Git commit; it excludes usernames,
bearer tokens, private URLs, coordinates and device identifiers.

**Green observations do not authorize flight.** They do not validate
autopilot calibration, wiring, battery health, EKF, RC, geofence, propellers,
flight commands, camera media, or regulatory conditions.

If this script rejects the M2 cache or Pi freshness, leave hardware control
unchanged. Check Windows LINK, backend /health, Pi serial ownership and the
read-only M2 manual. Never "fix" the test by enabling MAVLink command
forwarding, remote arming or parameter writes.

## Separate acceptance gates

The native Mission Planner UI, plugin loader, two camera streams, parameter
transactions, secure network failure injection and local radio/USB recovery
must still be manually tested on Windows and the disarmed bench.

This script is a diagnostic **preflight**, not a production pilot authorization
or flightworthiness certificate. It cannot replace the combined test signoff.
