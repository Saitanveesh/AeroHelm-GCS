# AeroHelm GCS operator workspace — development deliverables

**Branch:** `feat/aerohelm-workspace-vision-mission` (based on `feat/aerohelm-cloud-plugin`).

The hardware/cloud setup does **not** have to be powered on to inspect, build, or test
the code. This feature branch is intentionally separate from the working GCS
branch. Do not deploy to flight hardware without build and acceptance checks.

## What's implemented

- **SURVEY** WinForms window: enter North/South/West/East, row spacing,
  relative altitude and nominal speed; generate a serpentine/lawnmower
  pattern; validate bounded geometry; show route metrics and estimate;
  export **QGC WPL 110** waypoint draft for Mission Planner's Flight Plan
  -> Load WP File. This NEVER writes to the aircraft, arms, starts an
  autonomous mission, modifies a geofence, or uploads a flight plan.
- **VISION** camera-icon toolbar button: saves a nonsecret private
  HTTPS stream URL locally and opens the browser viewer. This is a
  launcher **not** a built-in WebRTC video decoder; camera capture and
  MediaMTX must be configured on the Pi first. It does not claim video
  is already live.
- **LINK** panel: reads `/health` and `/api/v1/mavlink/health`
  over private HTTPS, reporting Pi link freshness, frames and viewer
  count. It is read-only and should never be treated as a flight-ready
  signal.
- **CLOUD** remembers only the nonsecret cloud origin across launches;
  never the operator password or bearer token.
- **AeroHelm branded title installer**: runtime `logo.txt`
  and optional user-supplied `logo2.png`, `icon.png`,
  `splashbg.png`. Originals are backed up. Do NOT remove
  upstream GPL/copyright notices or strip ArduPilot attribution.
- Portable offline survey test runner and GitHub Actions CI.

## Compatibility

The existing Mission Planner Fly / Plan / Setup / Config windows and
native COM / TCP / UDP transport remain. The M1 cloud profile still
has no command path and must not be used for calibration, automated
flight, firmware flashing, motor tests or parameter writes.

The mission-draft `QGC WPL 110` format is documented at
https://mavlink.io/en/file_formats/ and is imported **offline**.

## Testing after Windows is available again

1. Close Mission Planner.
2. In `C:\Users\saita\AeroHelm-GCS`, fetch branch and switch:

   ```powershell
   git fetch origin
   git switch --track origin/feat/aerohelm-workspace-vision-mission
   ```

   If a local branch exists already, use
   `git switch feat/aerohelm-workspace-vision-mission`
   and `git pull --ff-only`.

3. Test the offline geometry/export module:

   ```powershell
   dotnet run --project Tools/AeroHelmSurveyTests/AeroHelmSurveyTests.csproj --configuration Release
   ```

4. Build with Visual Studio's full MSBuild (the known-good build script):

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\Build-AeroHelmGCS.ps1
   powershell -ExecutionPolicy Bypass -File .\scripts\Install-AeroHelmCloud.ps1
   ```

5. Optional branding: `powershell -ExecutionPolicy Bypass -File .\scripts\Install-AeroHelmBranding.ps1`.
   To apply an existing exact trademark artwork, use
   `-LogoPath 'C:\path\approved-logo.png'` and, optionally,
   `-IconPath 'C:\path\approved-icon.png'`.
   Do not upload fonts, private keys or cloud credentials as brand assets.

6. Launch `bin\Release\net461\MissionPlanner.exe`. Verify **CLOUD,
   SURVEY, VISION, LINK** buttons, offline export, URL validation and
   diagnostics. Check that the main Flight Data screen is unchanged.

7. Only after the normal Pi/Oracle binary uplink is running,
   test the separate M1 read-only native HUD mode. Don't accidentally
   connect a radio COM port or issue flight commands.

## Safety and limitations

- SURVEY coordinates are user-defined; no preset physical area and no
  airspace/terrain/regulatory database is bundled. Nominal distance/time
  excludes takeoff, wind, turns, climb, reserves and terrain.
- Offline survey paths **must** be checked by a qualified operator
  before flight. Successful import in Mission Planner does not indicate
  the mission is safe, legal or flyable.
- The exported home row is a placeholder. Verify the real takeoff/home
  position and flight controller mission semantics.
- Camera 1 is the only hardware-confirmed IMX708 source in the project;
  do not add a Camera 2 switch until camera 2 enumerates.
- Private Tailscale video is not equivalent to application-user
  authorization or a production security audit.
- Neither this plugin nor the current M1 bridge runs the autopilot.
  Cloud flight authority requires a separate audited bidirectional
  protocol, RC takeover, link-loss/freshness policy, geofence, SITL
  and supervised hardware qualification.

## Follow-on work (not implemented here)

- Embedded WebView2 WebRTC player and per-camera status; test against
  MediaMTX over the tailnet; no arbitrary public URLs.
- Map-native survey polygon editing, terrain verification,
  fence-aware path trimming and battery model.
- M2 read-only command/ACK relay, M3 supervised calibration
  with operator presence and independently enforced Pi-local policy.
- Login-on-launch and automatic *view-only* connection, then
  installer and trademark pack after UI/compiler validation.
- Full onboard mission authority is deliberately not a quick
  toggle or a raw socket passthrough.
