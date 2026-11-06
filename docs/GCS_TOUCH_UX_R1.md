# AeroHelm Ground Station — Touch UX R1

**Authoritative code repository:** `Saitanveesh/AeroHelm-GCS`
**Feature branch:** `feat/gcs-touch-ux-readonly-fleet-r1`, based on
`feat/aerohelm-duplex-link-console` (native Mission Planner GCS work).
**Change scope:** UI presentation, safe navigation and read-only API compatibility.
No native HUD renderer, serial transport, FC parameter, arming or mission command changes.

## Operator view

```text
┌─────────────────────────────────────────────────────────────────┐
│ ← NATIVE GCS        AEROHELM / PILOT WORKSPACE        CLOSE      │
├──────────────────┬───────────────────┬──────────────────────────┤
│  FLIGHT (F1)     │  PLAN (F2)        │  VISION (F3)             │
│  Choose path     │  Native map       │  Camera monitor          │
├──────────────────┼───────────────────┼──────────────────────────┤
│  OPS (F4)        │  SECURITY (F5)    │  FLEET (F6)              │
│  Workflow        │  Audit/identity   │  Aircraft overview       │
├──────────────────┼───────────────────┼──────────────────────────┤
│  LINK (F7)       │  ANALYTICS (F8)   │  SETTINGS (F9)           │
│  Diagnostics     │  Cloud summaries  │  Session config          │
├─────────────────────────────────────────────────────────────────┤
│ PILOT RADIO / MANUAL CHECK REQUIRED    [CHECK RADIO  F10]       │
│ CLOUD VIEW / HEARTBEAT + ATTITUDE ...  [REFRESH CLOUD]          │
│ LOCAL TCP / RECEIVE ONLY — separate from physical radio        │
└─────────────────────────────────────────────────────────────────┘
```

All tiles remain native WinForms controls with the original vector-rendered
icons: **no embedded Chromium, no new animation framework, no video decoding
inside the launcher, and no new GCS background worker service**. Tile
subtitles display when a tile has sufficient space, then disappear at small
sizes while accessible labels/tooltips remain available.

### Critical status boundaries

**Three independent signals are shown**, deliberately never combined:
1. **Pilot radio**: always `MANUAL CHECK REQUIRED` in the HOME launcher.
   The GCS cannot infer an independent backup radio from a cloud WebSocket
   or localhost TCP listener. Clicking `CHECK RADIO` merely opens
   `AeroHelmPilotDirectWindow`: connecting still needs an on-site operator's
   explicit port/baud selection and action. No automatic failover.
2. **Cloud view**: populated only from authenticated cloud health and real
   heartbeat/attitude evidence. A green/verified readout is still
   `VIEW ONLY`. Unknown, auth failure and stale samples show warnings.
   On focus loss, minimize or return from a modal screen, previous live
   status is invalidated and must be checked again.
3. **Local TCP adapter**: the dedicated 127.0.0.1 receive-only bridge's
   state/quality. It is **not** the independent physical pilot radio.

### Keyboard and touch

- `F1` to `F9`: the nine existing tiles in visual reading order.
- `F10`: open the independent pilot-radio panel, **not** connect.
- `Esc`: leave HOME for native Mission Planner. Existing flight controls
  and Mission Planner toolbar remain accessible.
- Touch target sizes: HOME panel radio check is 50px high, read-only page
  action buttons 46px, fleet rows 44px. All use native DPI scaling.
- At constrained screen dimensions, workspace icons/text scale down and
  subtitles hide. Multi-row read-only actions wrap and can scroll.

### Light-GCS behavior

Timers are bounded and **paused** on minimized/inactive HOME and while
modal child windows are open. Observational cloud requests are not
duplicated by nested screens. There is no additional telemetry reader,
historical log processor, ML model, local fleet planner, or periodic
window repaint timer.

The FLEET page now attempts the existing `GET /api/v2/fleet` API first,
falls back on HTTP 404 to the cloud control-plane
`GET /api/v1/fleet/status`, then (404 only) the legacy M1
`GET /api/v1/vehicles`. It never silently falls back after a 401/403,
timeout or malformed cloud JSON.

The cloud-control-plane response is capped at 128 aircraft on the GCS,
requires `cloud_flight_authorized=false`, and treats reported samples older
than five seconds as stale. Stale aircraft have UNKNOWN/empty mode, armed,
battery and GNSS values rather than old data misrepresented as live.
Virtual UAVs are marked `SIMULATED`; actual hardware is
`DEVICE REPORTED`, **not independently attested**.

`Cloud metrics` (FLEET) and `Cloud portfolio` (ANALYTICS) read the new
`GET /api/v1/fleet/metrics` server aggregation; the GCS only displays its
counts and states. This is read-only and invokes no cloud mission/task
dispatch API. If the new cloud service is not available, the screen clearly
reports the missing capability. The separate cloud R2 PR is **not merged or
deployed by this UI branch**, so live support is environment-dependent.

## Validation / rollout contract

Static no-aircraft source checks:

```bash
python -m unittest -v Tools.test_aerohelm_touch_ux
```

The native Windows workflow `.github/workflows/aerohelm-cloud-plugin.yml`
is configured to include this feature branch and run the source contracts,
.NET Framework plugin build and Mission Planner solution build.

Before merge and real operator use, obtain **passing exact-head GitHub CI**
and an actual WinForms UI smoke at 100%, 150%, 200% Windows scaling, at
1024×768 and 1920×1080, using touch, keyboard and a mouse. Verify:
- navigation back to the original Flight Data and Flight Planner;
- F1–F10 shortcuts without accidental duplicate windows/commands;
- read-only cloud permission denial and stale/unknown states;
- real independently wired radio control still requires pilot selection;
- GCS idle memory/CPU and per-window network polls before and after change;
- focus loss / modal child / minimize do not leave a green stale status;
- backend cloud R2 network/API availability is **not assumed**.

CI and Windows runtime acceptance have NOT been run or certified in this
file; no field aircraft was commanded and no deployed GCS was replaced.
