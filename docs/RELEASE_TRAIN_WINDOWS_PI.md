# AeroHelm Windows + Pi release train — consolidated acceptance

**Scope:** Windows Mission Planner first. iOS/Android are deferred. This
release track is source integration only, not flight-ready production code.

## Source-of-truth development stack

| Component | Development branch | Release gate |
| --- | --- | --- |
| Windows GCS / touchscreen | Saitanveesh/AeroHelm-GCS, feat/aerohelm-touch-console (draft PR #6) | Windows native MSBuild, plugin load, nine-page UX |
| Windows M2 transport | Saitanveesh/AeroHelm-GCS, feat/aerohelm-m2-readonly-transport (draft PR #5, ancestor) | Complete read-only parameter load; no outbound command bytes |
| Cloud / Pi M2 backend | Saitanveesh/AeroHelm, feat/aerohelm-m2-readonly-transport (draft PR #14) | Backend CI, private Oracle/Pi staging |
| Mobile browser PWA | Saitanveesh/AeroHelm, feat/aerohelm-touch-console (draft PR #15) | DEFERRED |

The alternative GCS touchscreen draft PR #7 was not the integration root.
Its fixed 3×3 layout is consolidated with the vector icon implementation
in PR #6 through the merged Windows development batches. Do not promote
both competing touch implementations to stable.

## One combined supervised acceptance session (later)

A. WINDOWS OFFLINE, AIRCRAFT OFF

From a clean Windows PowerShell in the existing development checkout:

    cd C:\Users\saita\AeroHelm-GCS
    git fetch origin
    git switch feat/aerohelm-touch-console
    git pull --ff-only
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Inspect-AeroHelmWindowsStage.ps1 -Stage Source

Only after backup of the working installation and review of output paths,
build the native solution and plugin:

    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Inspect-AeroHelmWindowsStage.ps1 -Stage Build -SkipBranding
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Touch.ps1 -ManualTouch

The native build command may replace files in the repository build output.
The inspector writes a reproducible hash-only local manifest under ignored
build-logs/aerohelm-windows-readiness.json. It does not record credentials,
location, telemetry or private cloud URLs. It explicitly marks hardware and
human GUI acceptance FALSE even if compilation succeeds.

Verify 3x3 tile layout, vector icon shapes, 100/125/150% Windows DPI,
mouse/touch/keyboard and HOME/ESC. FLIGHT/PLAN must open the real Mission
Planner native views, not browser approximations. Check all seven other
module pages and existing INTEL/USB recovery entry points.

B. ISOLATED BACKEND SIMULATION, NO PIXHAWK

Using the reviewed AeroHelm backend development checkout and existing
scripts/Start-Local-AeroHelm-Sim.ps1 on loopback, test OPS software lease,
simulation start/pause/resume/abort, no hardware remote mission execution.
Software simulator outcomes are not ArduPilot SITL or field acceptance.

C. PRIVATE CLOUD + PI + PIXHAWK BENCH

Power only with Pixhawk DISARMED and propellers removed. First back up
working Oracle deployment, Pi environment and Windows GCS binary. Keep
the private Tailscale tailnet and one Pi UART reader on verified TELEM1.

Review the full M2 instructions in AeroHelm/docs/M2_NATIVE_READONLY_PARAMETERS.md.
Verify actual fresh telemetry in native Mission Planner. The GCS read-only
parameter responder must refuse PARTIAL, stale, unverified, or inconsistent
snapshots. Check that PARAM_SET, ARM, mode change, mission upload and
arbitrary outgoing MAVLink bytes never cross the cloud. Disconnect Pi
network and confirm link STALE or UNAVAILABLE; do not show historic data
as current.

D. VIDEO AND ANALYTICS

Camera 1 and optional Camera 2 require independently confirmed Pi hardware
and private HTTPS WebRTC endpoints; opening VISION is not proof of stream.
Use direct private Pi video, not Oracle micro VM relay. Verify registry
selection, battery/altitude historical reports, offline archive labels,
security posture, bounded RAM audit and session-expiry/re-login behavior.

E. RELEASE AND RECOVERY SIGNOFF

Record Windows build exit code, plugin load, actual UI screenshots,
network fault injection, test UTC time, backend/GCS commit SHAs and
rollback. Any failed gate blocks production merge/deployment. Preserve
native USB/radio recovery. Never enable public Funnel or unreviewed
remote aircraft-command forwarding.

## Remaining engineering track after Windows UX

- Signed Windows packaging and tested rollback, compatibility matrix
- Proper long-term audit storage and telemetry retention with bounded load
- Per-device identity and role/MFA policies; optional Kerberos belongs
  at a gateway, never on a real-time Pi telemetry loop
- Verify both physical camera devices and dedicated low-latency video
- Separate SITL/hardware/field safety and regulatory program before
  consideration of physical autonomous commands

**No physical remote mission, arming, cached-parameter writes or
unsupervised calibration is authorized by this development work.**
