# AeroHelm Touch Workspace — nine modules, desktop + mobile companion

## Product model

The user-supplied reference consists of a 3×3 black/charcoal touch grid:
FLIGHT | PLAN | VISION
OPS | SECURITY | FLEET
LINK | ANALYTICS | SETTINGS

**Mission Planner is still the root flight-control application.** The
AeroHelm Windows plugin opens a 3×3 full-screen operator HOME overlay, with
large hit targets, keyboard focus/ESC support, DPI scaling and clear back
navigation. No new MAVLink command origin is introduced.

The independent companion at `/touch` runs in a mobile browser on the **private
Tailnet HTTPS backend**, sharing its exact same origin to avoid an unnecessary
CORS allowance. Browser mode is **read-only**, not a mobile piloting device:
no ARM, TAKEOFF, RTL, mission upload, calibration or parameter-write UI.
Use Tailscale on the phone. Do NOT enable Tailscale Funnel or publicly
expose Oracle port 8000.

## Windows tile mapping (source integrated)

| Touch tile | Desktop destination |
| --- | --- |
| FLIGHT | Native Mission Planner Flight Data view |
| PLAN | Native Mission Planner Flight Planner view |
| VISION | AeroHelm private HTTPS camera viewer/launcher |
| OPS | AeroHelm simulation and locally approved maintenance UI |
| SECURITY | INTEL directly opens SECURITY tab |
| FLEET | INTEL directly opens FLEET tab |
| LINK | AeroHelm cloud link diagnostic form |
| ANALYTICS | INTEL directly opens HISTORY/report tab |
| SETTINGS | Native Mission Planner Config/Tuning |

A HOME button in Mission Planner opens the overlay. The existing CLOUD,
SURVEY, VISION, LINK, OPS and INTEL toolbar entries remain until a later UX
pass consolidates them into a single accessible navigation bar.

## Build and launch on Windows (aircraft not required)

```powershell
cd C:\Users\saita\AeroHelm-GCS
git fetch origin
git switch --track origin/feat/aerohelm-touch-workspace
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Integrated-AeroHelm.ps1
```

If the local branch already exists, use normal `git switch` and
`git pull --ff-only`. The build needs **Visual Studio MSBuild** for the
Mission Planner full-framework solution. Do not copy half-built DLLs or
overwrite the existing working GCS build if compilation fails.

To open the HOME panel automatically at GCS startup:

```powershell
$env:AEROHELM_TOUCH_HOME="1"
Start-Process ".\bin\Release\net461\MissionPlanner.exe"
```

Without that flag, click **HOME** in the Mission Planner toolbar. Close the
overlay (or press ESC) to return to the original Mission Planner interface.

Note: startup cloud authentication is still tied to the branded product
profile; a normal standalone Mission Planner build is not replaced by this
plugin. To test UI without cloud: launch with `--local-maintenance` as
documented previously, not as a flight-permission bypass.

## Phone / tablet companion

The separate backend PR adds `GET /touch` to the existing private FastAPI
cloud app. After deploying the reviewed server branch, open
`https://aerohelm-vcn.tailf3ab6a.ts.net/touch` in a phone's browser **while
connected to the same Tailscale tailnet**. No Pi camera or Pixhawk needed
for the touch menu's basic layout.

The browser uses normal same-origin bearer authentication, keeps access token
only in JavaScript memory, sends no cookies or writes, and provides:
- FLIGHT: registered fleet and alarms (telemetry view only)
- PLAN: user-entered offline route/geofence validation (server returns
  `flight_authorized:false`)
- VISION: launch an independently configured private Pi HTTPS camera URL
- OPS: capability/status only; mission actions deliberately unavailable
- SECURITY: posture and bounded audit
- FLEET: authenticated fleet inventory
- LINK: read-only MAVLink health
- ANALYTICS: bounded report, current and archived history
- SETTINGS: cloud security posture and local-MP advisory

The page is supplied from the server itself, protected by CSP and
anti-framing headers. A browser session is NOT an operator flight
authorization. Mobile view does not attach to or control the local
Windows Mission Planner process.

## Release/acceptance checklist

1. Windows plugin compiles with `AeroHelmTouchWorkspace.cs` included;
   verify nine large touch hit areas on 1920×1080 and 1280×720.
2. HOME opens in GCS. FLIGHT / PLAN / SETTINGS open the intended native
   Mission Planner tabs. ESC returns to Mission Planner.
3. Other tiles open existing forms and close without crashing/dangling
   event handlers. Confirm cloud token login remains unchanged.
4. Simulate portrait phone viewport at ~390 px: nine tiles remain legible,
   responsive with keyboard/assistive labels.
5. From a phone on Tailnet, open /touch. Unauthenticated data APIs reject
   with HTTP 401. After login, FLEET/LINK and SECURITY show the correct data.
6. Network outage marks LINK as failed; no stale state is interpreted as
   live hardware readiness.
7. A phone cannot cause an ARM/MISSION/PARAM_SET/calibration request because
   no such browser UI or route is implemented here. No public video endpoint.
8. Keep M1/M2 Cloud-MAVLink hardware controls disabled until separate
   authenticated per-action policy and bench/SITL safety gates are met.

## Pending product work

- Replace legacy Mission Planner visual chrome with consistent AeroHelm
  navigation, without destroying upstream plugin/config/diagnostic access.
- Integrate video using embedded WebView2, after media server and second
  camera hardware are qualified.
- Offer a signed Windows distribution and a phone home-screen PWA after
  test/branding assets are finalized. No native phone flight controls.
- Full remote mission/configuration is independent of this touch interface
  and remains disabled for the physical aircraft.

Do not merge either PR into a flight-ready build until MSBuild, browser
and real Pixhawk/Pi safety testing have been completed.
