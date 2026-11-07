# AeroHelm single-login Windows acceptance, receive-only M1

**Development candidate only**. Review, source-contract tests and a native
Windows build must pass before replacing the proven Mission Planner runtime.
Do not deploy unvalidated files to the powered-down Pi or Oracle.

## User experience

1. Close any Mission Planner and old Python bridge (port 127.0.0.1:5760).
2. From `C:\Users\saita\AeroHelm-GCS`, pull the reviewed GCS branch.
3. Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Session.ps1`.
4. **Login first.** Credentials are sent over verified Tailscale HTTPS,
   handled by the existing Oracle bearer authentication service and kept
   in memory only. Cancellation exits the application.
5. Mission Planner opens the full-screen 3x3 TOUCH panel. Help and Simulation
   toolbar buttons are hidden *in this launcher profile* (not deleted).
6. The GCS itself listens on loopback TCP 127.0.0.1:5760 and forwards
   authenticated cloud MAVLink bytes. Click the FLIGHT touch tile to request
   the native connection automatically; no IP/port prompts in this profile.
   Native Mission Planner still REQUIRES actual Pixhawk heartbeat packets
   and skips parameter retrieval on this strictly read-only M1 path.
   If the heartbeat is absent, the HUD must not report successful connection.
   **Every outbound GCS byte is discarded**.
7. Clicking VISION preloads the existing private Pi camera URL and opens
   the browser viewer. Camera video is separate from telemetry and is not
   transported via Oracle.

No separate Python bridge or second login is necessary **if this new
native adapter passes its Windows hardware acceptance**. Keep the known-good
Python bridge as an explicit fallback if the new adapter fails.

### Legacy fallback

```powershell
# Requires a separate masked bridge sign-in, and never runs the native bridge.
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Session.ps1 -UseLegacyPythonBridge
```

Or if the Python bridge is already running, use `-SkipBridge`.
An existing process on TCP 5760 prevents native single-login startup.
**Do not kill unknown listeners or disable firewall protections to proceed.**

### Maintenance / rollback

To keep upstream Help and Simulation navigation visible in the reviewed
launch profile, pass `-ShowNativeToolbar`. For native offline recovery,
launch `MissionPlanner.exe --local-maintenance` directly; this does NOT
grant cloud credentials, physical flight permissions or remote commands.
Use existing DLL rollback procedure in `scripts/Restore-AeroHelmPlugin.ps1`.
Do not replace the known working build until new native compilation succeeds.

## Mandatory real-system gates

- **GCS build:** source contracts + full Visual Studio MSBuild + native plugin load.
- **Authentication:** correct login opens TOUCH; wrong/cancelled login does not;
  JWT expiry requires reauthentication; no tokens on disk/arguments/logs.
- **Bridge:** loopback listener only, exclusive port, authenticated WSS,
  increasing real HEARTBEAT/ATTITUDE counts, fragmented WebSocket messages,
  oversized/invalid frame rejection. The native TCP reader must never send
  anything back to Oracle or Pi. Compare against the proven Python bridge.
- **HUD:** propeller-less, disarmed Pixhawk physically tilted gently:
  real roll/pitch must move. `frames_received` increasing *alone* is not
  sufficient. If HEARTBEAT or ATTITUDE=0, inspect Pi/FC streaming.
- **VISION:** test real video, stale media, reconnect, Tailscale ACL and
  certificate. HTML HTTP 200 alone is not proof of moving video.
- **OPS/SECURITY/FLEET/ANALYTICS:** the deployed M1 Oracle server lacks
  several v2 endpoints; backend rollout/compatibility must be validated
  independently before considering these panels complete.
- **LINK:** stale/disconnected state must be prominent and fail closed.
- **PLAN:** no mission execution, upload or parameter write over M1.

## Phase scope

This change makes launch/setup more predictable. It does **not** authorize
autonomous physical flight, supervised calibration, parameter mutation,
firmware change, telemetry rate override, or a remote MAVLink command channel.

The operator toolbar hides HELP and SIMULATION by default and provides native
vector icons for each AeroHelm module. These upstream controls remain in the
offline native maintenance executable rather than being deleted.

The Pi M1 uplink and Oracle hub now have passive heartbeat/attitude counters
in merged development code; they are not automatically deployed to the user's
running Pi or Oracle. The backend must be staged/bench-verified separately.
