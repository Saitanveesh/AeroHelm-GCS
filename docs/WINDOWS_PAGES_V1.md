# AeroHelm Windows pages — native touch development batch

## Scope

**Windows Mission Planner only.** Native Mission Planner continues to own flight
telemetry, flight planning and physical vehicle controls. This batch builds the
Windows pages behind the existing exact 3×3 monochrome TOUCH grid. iOS/Android
PWA work is intentionally not promoted or modified.

Development source: `feat/aerohelm-windows-pages-v1`, branched from
`feat/aerohelm-touch-console`. Incorporate into that existing Windows draft
branch only after code review; do not merge into `master` before release tests.

## Actual page behavior

| Touch tile | Native destination | Data contract / qualification |
| --- | --- | --- |
| FLIGHT | Mission Planner Flight Data | Native GCS, no new cloud control |
| PLAN | Mission Planner Flight Planner | Native GCS, not remote mission execution |
| VISION | Private dual-camera launcher | Two locally saved independent private HTTPS/loopback links; no false live-video indicator |
| OPS | Existing simulated mission, supervised maintenance and authenticated read-only CAPABILITIES page | Simulation-only autonomy; local approval required for permitted calibration |
| SECURITY | New read-only status page | `GET /api/v2/security/posture`, `GET /api/v2/security/audit?limit=75` |
| FLEET | New read-only fleet page | `GET /api/v2/fleet`, selected aircraft `/alerts` |
| LINK | Existing diagnostics upgraded | `/health` and `/api/v1/mavlink/health` with explicit fresh/stale/unknown; optional 15s polling |
| ANALYTICS | New read-only reporting page | Bounded `/report`, `/history?limit=100`, `/archive?limit=100` |
| SETTINGS | New Windows session/settings page | Session expiry status, CLOUD link, read-only parameter cache, native MP Config/Tuning |

The existing INTEL toolbar remains accessible for **PREFLIGHT**, route
integrity, passive mission shadow, parameter comparison, raw source audit and
export. This batch does not remove any existing tools. It only provides a
more usable touch dashboard for the requested domains.

### Data-integrity guarantees

- A failed cloud request **clears** previously displayed rows and marks the
  page `UNAVAILABLE / NOT VERIFIED`; stale values are never quietly reused.
- A fleet aircraft is labelled fresh only when the authenticated cloud fleet
  API returns `online: true`. This is a link-freshness observation, **not**
  flight readiness or permission to fly.
- Historical and archived telemetry is explicitly identified and never
  presented as current GNSS or battery state.
- The SECURITY page reports the cloud's own limitations: in-memory audit,
  unverified MAVLink signing, no full enterprise multi-device authority.
- Read-only pages perform **GET** requests using the existing login JWT;
  there is no new write method or device command path.
- A camera link can be saved or opened but the app does not assert a video
  stream exists. The optional second Pi camera requires separate hardware
  testing. Camera URLs may not contain credentials, query strings or fragments.
- LINK opt-in monitoring is bounded to one request at a time and stops when
  the page closes. Failure cannot grant remote control.

## Source and offline checks

```powershell
cd C:\Users\saita\AeroHelm-GCS
git fetch origin
git switch -c feat/aerohelm-windows-pages-v1 --track origin/feat/aerohelm-windows-pages-v1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Integrated-AeroHelm.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Touch.ps1
```

Use `git switch` and `git pull --ff-only` if the branch already exists.
Keep the existing working installed GCS binary backed up and closed during
build. Avoid overwriting it when any check fails.

## One combined acceptance session, later

1. **Windows-only**: full solution and plugin MSBuild; no missing C# references;
   inspect Mission Planner plugin loader and single toolbar instance.
2. **Touch/navigation**: validate all nine modules with keyboard/mouse/touch,
   100–150% DPI scaling, 1280×720 and 1920×1080 layouts, ESC return.
3. **Disconnected cloud**: new FLEET/SECURITY/ANALYTICS fail closed with
   visible `UNAVAILABLE` and no retained stale state; LINK reads `UNAVAILABLE`.
4. **Private cloud**: login, authentication expiry, correct aircraft-specific
   report/alerts, audit and fleet fields; check empty-state views.
5. **Cameras**: camera 1 and 2 independently configured; test HTTPS and actual
   Pi MediaMTX/WebRTC pages only after corresponding camera hardware is online.
6. **Pixhawk / Pi bench**: disarmed, propellers removed, known backup config;
   M1/M2 native telemetry, verified *complete* cached parameters and rejection
   of any GCS-originated actuation through the cloud. Do not use cached
   parameters for aircraft configuration or preflight readiness.
7. **Rollback**: preserve and verify older GCS executable/plugin, original
   private cloud deployment, Pi agent and Tailscale configuration.
8. **Release decision**: only after Windows compiler, human UI, Pi hardware,
   and safety checks pass should stacked draft branches be considered for
   stable promotion. No public Funnel or unreviewed remote flight access.

## Known limitations / not claimed

- No live camera preview embedded in Mission Planner; VISION opens the
  trusted private browser page.
- No mobile piloting or iOS/Android deployment.
- No certified physical autonomy or unrestricted MAVLink remote control.
- No verified Windows executable from this development environment.
- No hardware qualification, production-grade durable audit, MFA,
  signed distribution, fleet provisioning or tested LTE behavior yet.

The offline PowerShell contract test is **not** a compilation test, and neither
test is an aircraft qualification. CI execution itself must be confirmed
separately; GitHub Actions may not currently execute on this repository.
