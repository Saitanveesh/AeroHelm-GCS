# AeroHelm Windows LINK — bidirectional diagnostic acceptance (development)

**GCS branch:** `feat/aerohelm-duplex-link-console` stacked on
`feat/aerohelm-cloud-link-quality-v1`. Companion Oracle/Pi branch:
`Saitanveesh/AeroHelm` → `feat/aerohelm-bidirectional-management-v1`.

Source staged only. **Not deployed or Windows/hardware verified**.

## Operator entry point

Open the native nine-panel AeroHelm GCS → **LINK**. Existing automatic
cloud/HEARTBEAT/ATTITUDE monitoring remains unchanged. The LINK panel now
provides two explicit actions:

- **PI ROUND-TRIP PING**: authenticated request/ACK to Oracle, then Pi.
  The server returns a correlated ACK and elapsed request/ACK time.
- **PI UPLINK COUNTERS**: return actual existing Pi binary WSS uplink
  counts, heartbeat and attitude counts, queue depth and dropped frames.
  No extra Pi serial reader, FC request or parameter write.

The LINK panel includes short `DUPLEX / ACK VERIFIED` or
`DUPLEX / UNAVAILABLE` state, plus complete result in the read-only text
region. A 15-second periodic cloud health check continues separately;
the new sideband probes are **deliberate operator actions**, not periodic
MAVLink commands. Manual probes are serialized in the UI.

The GCS uses the existing operator login JWT through `OperationsApi`.
It refuses to send the token to a manually entered LINK URL unless the
hostname, scheme and port exactly match the signed-in private cloud origin.
If Oracle has not been deployed with the v1 sideband, LINK reports it as
unavailable and the native Mission Planner HUD/physical radio remain
unchanged.

The backend requires `AEROHELM_DUPLEX_DIAGNOSTICS_ENABLED=true` on
both Oracle and Pi, provisioned separately during supervised maintenance.
Never enable this for flight commands. The server accepts ONLY `ping`
and `uplink_status`. There is no MAVLink downlink or cloud aircraft
control authority.

## Source checks (no aircraft)

From the correct GCS branch, without deleting local work:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git status --short
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
python -m unittest -v Tools.test_aerohelm_offline_gcs Tools.test_aerohelm_build_globs
```

The existing Windows session launcher and transactional release-consolidation
script remain deliberately pinned to the previously reviewed integration
branch. They will **refuse to launch this new feature branch** until a
separately reviewed consolidated release candidate is prepared. Use the
read-only source checks and `scripts/Build-AeroHelmGCS.ps1` with the
`-Target Solution` and `-Target Plugin` options for developer-only
compilation; do not manually copy its DLL into the verified runtime.

Native MSBuild, plugin SHA-256 manifest checks, Windows UI smoke tests,
real JWT authentication, Oracle/Pi rollout, and disarmed propeller-free
hardware acceptance are **still separate mandatory release gates**.

## Acceptance matrix

| Condition | Expected result |
| --- | --- |
| Backend diagnostics off | HTTP 503 / GCS DUPLEX UNAVAILABLE; HUD unchanged |
| Pi sideband disconnected | HTTP 503; no queued remote work or FC writes |
| Valid JWT + correct Pi device | One random correlation ID, ACK, measured RTT |
| Invalid JWT or mismatched origin | No diagnostic request, no JWT leakage |
| Duplicated or late Pi ACK | Rejected; cannot revive an expired request |
| Connection reset during request | LINK_LOST or TIMEOUT, never auto-retry aircraft operations |
| Camera/video or native HUD active | Separate streams and state; no new UART owner |
| Physical telemetry radio | Manual pilot selection only; no auto failover |
| Unexpected request action (arm, mode, param_set, mission upload) | Rejected before Pi execution |

The two cloud management directions are software transport capability,
**not** remote aircraft-control readiness.
