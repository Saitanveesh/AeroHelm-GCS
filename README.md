# VIHANGA Ground Station

**Native UAV ground operations with a private-cloud telemetry extension.**

Vihanga (విహంగ) is a Mission Planner-derived ground station for flight data,
mission preparation, operator connectivity and fleet inspection. The project
retains native USB and telemetry-radio support for on-site pilots alongside a
separate authenticated, receive-only cloud telemetry path.

**Development status:** Phase-3 integration candidate. Source wiring and
isolated tests do not establish field readiness. Cloud, Windows UI, physical
link and aircraft acceptance must be completed before real operations.

## Operator interface

| Control | Role |
| --- | --- |
| DATA, PLAN, SETUP, CONFIG | Original Mission Planner flight, planning and configuration interfaces |
| CLOUD | Authenticate and inspect registered cloud vehicles |
| PATHS | Manually choose cloud receive-only viewer or pilot-direct USB/radio |
| SURVEY | Validate/export offline route drafts |
| VISION | Open configured private camera streams |
| LINK | Check cloud/Pi health, freshness and diagnostics |
| OPS | Simulation missions and locally approved bench maintenance requests |
| INTEL | Fleet, advisory preflight, parameter drift, history and security |
| MORE | Fleet, Security, Analytics, Parameters and Settings |

OPS simulation execution is not aircraft command authority. Remote cloud
missions and parameter writes to physical aircraft are not enabled.
The cloud and local radio are separate; no automatic failover takes place.

## Architecture

```text
                  VIHANGA / Mission Planner (Windows)
                    |                        |
       private authenticated HTTPS/WSS   local USB / radio
                    |                        |
                cloud API             Pixhawk / autopilot
                    |
              Raspberry Pi agent
                    |
          flight-controller telemetry
```

The cloud path uses a local 127.0.0.1:5760 receive-only MAVLink bridge.
Outbound bytes are discarded by that bridge; native Mission Planner remains
the authority for direct local connections. Security and freshness checks
fail closed. Pilot/FC physical failsafe behavior is configured independently
of this software.

## Build, verification and deployment

The reviewed Windows GCS integration branch is
`feat/vihanga-phase3-native-toolbar`, stacked on the GCS development
branch `feat/gcs-touch-ux-readonly-fleet-r1` pending formal merge.

On Windows, build through Visual Studio/MSBuild and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
python -m unittest -v Tools.test_aerohelm_offline_gcs Tools.test_vihanga_native_toolbar
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Build -Apply -WithBranding
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Verify
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1
```

The release process checks source and installed binary hashes, backs up
the prior runtime, and stops if duplicate plugin files or an unknown TCP
listener are detected. The old `Start-AeroHelm-Touch.ps1` name remains
only as a command-line compatibility alias; it does not load TOUCH.

See [certification procedure](docs/VIHANGA_CERTIFICATION.md),
[Phase-3 handover](docs/VIHANGA_PHASE3_HANDOVER.md),
[Windows safe refresh](docs/GCS_SAFE_REFRESH_POWERSHELL.md), and
[upstream build reference](docs/UPSTREAM_BUILD_REFERENCE.md).

## Provenance and license

This is a derivative of
[ArduPilot Mission Planner](https://github.com/ArduPilot/MissionPlanner).
Original code, source attribution, upstream project identifiers and
GPL-3.0 license notices are retained. Product-facing Vihanga labels do
not imply ownership of upstream Mission Planner development. The existing
`AeroHelm` internal namespaces, scripts, DLL names and environment
variables are intentionally preserved for compatible upgrades. Consult
[COPYING.txt](COPYING.txt) and
[project provenance](docs/AEROHELM_PROVENANCE.md).
