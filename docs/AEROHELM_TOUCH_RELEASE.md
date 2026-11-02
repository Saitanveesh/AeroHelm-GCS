# AeroHelm touch console — architecture and combined acceptance

## Purpose

Implement the supplied monochrome nine-tile reference as a **working**
touch-first operator interface. Native Windows Mission Planner remains
the flight control foundation. The mobile companion is an independent
cloud read-only client; it does **not** run Mission Planner on the phone.

## Supported interfaces

| Tile | Windows Mission Planner TOUCH | iPhone / Android companion |
| --- | --- | --- |
| FLIGHT | Native Flight Data and HUD | Authenticated aircraft GPS, roll/pitch, battery, mode |
| PLAN | Native Flight Planner | Cloud draft geometry/geofence preflight and SHA-256 digest |
| VISION | Private camera viewer launcher | Opens Pi private Tailscale HTTPS camera page |
| OPS | Simulator operations and supervised maintenance | Cloud capability/status overview only |
| SECURITY | INTEL security tab | Security posture and audit |
| FLEET | INTEL fleet tab | Aircraft status and warnings |
| LINK | Existing link diagnostics | MAVLink freshness, queue drops, viewer count |
| ANALYTICS | INTEL analytics/history tab | Live historical report plus battery/altitude SVG charts |
| SETTINGS | Cloud registry, native MP settings, phone portal | Session, origin, installation guidance |

## Security boundary

- Only AeroHelm's existing API at the private Tailscale HTTPS origin serves
  /touch/; **no Node server, Docker process or separate Oracle VM**.
- JWT token only in browser-tab memory. Refresh clears session.
- Service worker caches only static app shell, never API data, auth, images
  from camera, telemetry, flight plans or secrets.
- Remote HTTP responses are never inserted as HTML; display uses textContent.
- Strict CSP, no iframe embedding, no unsafe-inline scripts/styles.
- Phone has no physical mission upload, calibration execution, arm, disarm,
  mode change, or arbitrary MAVLink command interface.
- VISION launches only HTTPS URLs whose hostname ends with .ts.net and
  which have no URL credentials, query or fragment.
- Mission Planner local USB/radio recovery remains available.
- Backend/Pi and GCS development are kept in separate draft PRs.

## Development branches

- Saitanveesh/AeroHelm — feat/aerohelm-touch-console (PR #15)
- Saitanveesh/AeroHelm-GCS — feat/aerohelm-touch-console (PR #6)

## Desktop Windows integrated test (without powering the aircraft)

Close existing Mission Planner windows. Use PowerShell:

    cd C:\Users\saita\AeroHelm-GCS
    git fetch origin
    git switch --track origin/feat/aerohelm-touch-console
    powershell -ExecutionPolicy Bypass -File .\scripts\Build-Integrated-AeroHelm.ps1
    powershell -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Touch.ps1

If the branch exists locally, switch and use git pull --ff-only instead.

The launcher sets AEROHELM_TOUCH_STARTUP=1, which opens the touch
dashboard **after** the ordinary GCS startup/login. Alternatively start
with -ManualTouch and click TOUCH in the normal GCS toolbar.

Verify all nine buttons, window resizing, Escape-to-close, Windows touch
and keyboard navigation, plus that FLIGHT/PLAN really open Mission
Planner native views. Existing plugin buttons are retained as fallback.

## Phone test (after safe cloud branch deployment)

An iPhone with Tailscale connected opens:

    https://aerohelm-vcn.tailf3ab6a.ts.net/touch/

The HTTPS certificate must validate; no public Funnel or port
forwarding is required. Log in using the existing operator account.
Check the nine tiles, responsive two-column phone layout, module/back
navigation, real telemetry when Pi is live, absent/stale link indication
when Pi is off, authenticated security/fleet/analytics data, and
non-actuating PLAN validation returning flight_authorized=false.

Reloading the PWA clears the browser token and requires fresh login.
For an installable icon, use Safari Share -> Add to Home Screen or the
Android browser install option. Without network, only the safe static
shell is available; **no offline telemetry is presented as live**.

VISION requires the separate Pi MediaMTX / WebRTC camera server to
actually be running. The second camera is not yet detected on hardware.

## Deployment gates

1. Backend/PWA automated Python checks and JavaScript syntax pass.
2. Full Windows MSBuild, plugin compile and actual touch UI smoke test pass.
3. Review backup/rollback of current Oracle systemd and Pi agent before
   deploying this branch; running deployments have not been changed.
4. Verify genuine fresh telemetry over Tailscale on Windows and phone
   with disarmed propeller-free Pixhawk.
5. Independent SITL, cloud authorization and physical hardware tests
   remain compulsory for future remote flight command features.

## What this batch does not claim

No embedded WebRTC decoder, physical autonomous mission execution,
public internet flight authority, mature fleet identity provisioning,
durable production audit, real terrain/weather legal verification,
or production-qualified full MAVLink command proxy.
