# Vihanga Ground Control Station

**Vihanga GCS** is a Windows ground-control station based on [ArduPilot Mission Planner](https://github.com/ArduPilot/MissionPlanner). It adds an authenticated private-cloud telemetry path from an actual flight controller to the native Mission Planner interface.

The source repository remains named `AeroHelm-GCS` for compatibility with existing build scripts, namespaces and installations. **Vihanga GCS** is the operator-facing product name.

## What works

An aircraft equipped with a Pixhawk and Raspberry Pi can send MAVLink telemetry over a private Tailscale connection to an Oracle Cloud relay. An authenticated Windows viewer can then show the real flight data in Mission Planner's native HUD.

During a supervised bench session on **8 November 2026**, the operator reported these results:

| Test | Observed result |
| --- | --- |
| Pixhawk serial port | Raspberry Pi 5 `/dev/ttyAMA0`, 57,600 baud, FC system ID 1 |
| Pi to Oracle | Authenticated device WebSocket connected |
| Oracle to Pi diagnostic | Matched round trip, 286 ms in one measurement |
| Physical MAVLink telemetry | HEARTBEAT ~1 Hz; ATTITUDE ~5 Hz; position and VFR_HUD ~2 Hz |
| Native Windows interface | Flight values visible in the GCS HUD and Quick data panel |
| Physical flight commands through cloud | **Disabled and not accepted** |

These are results from one aircraft and its operator logs, **not fleet-scale testing, flight certification, a network latency guarantee or evidence of airborne control**. A physical roll/pitch movement test, reboot recovery and independent direct-backup acceptance remain separate checks.

## How the connection works

```text
Pixhawk flight controller
   |  MAVLink serial telemetry
   v
Raspberry Pi 5
   |  /dev/ttyAMA0 (single serial-port owner)
   |  authenticated WebSocket over private Tailscale
   v
Oracle Cloud VM
   |  Vihanga relay on 127.0.0.1:8765
   |  Tailscale HTTPS :8443
   v
Windows Vihanga GCS
   |  signed-in viewer / receive-only loopback bridge
   v
Mission Planner DATA page, HUD and Quick view
```

The Pixhawk runs stabilization and onboard failsafes. The Pi reads its physical serial port. Oracle relays verified MAVLink data. The Windows application displays it. The existing Oracle API on HTTPS `:443` is a separate, older service and is **not** the Vihanga relay on `:8443`.

A local USB cable or telemetry radio is an **independent pilot-selected backup**, not an automatic takeover. It must be tested separately and must not share the Pi UART with another serial reader.

## Set up the Windows GCS

**Requirements:** Windows, Visual Studio 2022 with the C++/.NET workloads used by Mission Planner, .NET SDK for the offline test projects, and access to the authorized private tailnet. See [the upstream build reference](docs/UPSTREAM_BUILD_REFERENCE.md).

Close Mission Planner. In PowerShell, from the repository root:

```powershell
git submodule update --init --recursive
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Repair-AeroHelmWindows.ps1 -Apply
```

The repair command performs source checks, builds the Windows solution, installs the plugin and verifies recorded executable/DLL hashes. **A failed build restores the previous runtime; do not edit the release manifest to bypass a mismatch.**

For a previously configured machine with a verified build, start the private cloud viewer:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 `
  -NoAutoTcp `
  -LinkOrigin "https://YOUR-TAILSCALE-HOST.ts.net:8443" `
  -OracleInstanceOcid "YOUR-FULL-OCI-COMPUTE-INSTANCE-OCID"
```

In the GCS choose **PATHS → Cloud telemetry**, then sign in with the separate Vihanga relay operator credentials. Do not paste passwords, device tokens or private configuration files into issues.

## Set up the Pi and Oracle relay

These are **separate machines**. Never run Pi systemd or UART commands on the Oracle VM.

Follow the [physical link deployment guide](docs/VIHANGA_ORACLE_PI_LIVE_LINK_DEPLOY.md) to install and configure the dedicated services:

| Machine | Service | Purpose |
| --- | --- | --- |
| Raspberry Pi | `vihanga-pi.service` | Exclusive physical MAVLink serial reader and cloud publisher |
| Oracle VM | `vihanga-relay.service` | Private, identity-pinned relay and authenticated viewer endpoint |
| Windows PC | `Start-VihangaGCS.ps1` | Verified native desktop launch |

The Pi UART must use the actual connected physical pins and verified baud rate. A Pi 5's `/dev/ttyAMA10` is normally its **debug UART**; the 40-pin GPIO UART typically uses `/dev/ttyAMA0` when enabled.

An operator may request fixed **nonpersistent telemetry message intervals** on a physically disarmed, propellers-removed bench. That diagnostic does not authorize arming, takeoff, mode changes, mission execution or parameter writes. Do not leave maintenance opt-ins enabled for ordinary operation.

## Check whether telemetry is live

From the Oracle VM or an authorized tailnet device:

```bash
curl -fsS https://YOUR-TAILSCALE-HOST.ts.net:8443/api/v1/mavlink/health
```

For a genuine HUD data stream, check `device_online`, `fresh`, `real_heartbeat_frames`, `real_attitude_frames` and `native_quick_data_ready`. A WebSocket connection alone does **not** prove the flight controller is sending attitude data. Missing GPS should be reported as missing, never replaced with a made-up map location.

For a fuller authenticated transport test see [the live-link guide](docs/VIHANGA_ORACLE_PI_LIVE_LINK_DEPLOY.md). For the latest capability and acceptance matrix see [project status](docs/AEROHELM_STATUS.md).

## What is not finished

Cloud flight-control commands are **not enabled**. The software currently does not provide USB-equivalent remote arming, flying, mission upload or general parameter writes. The cloud-side supervised command model is a **simulator-only safety contract**. Multi-aircraft fleet management, enterprise Kerberos SSO, autonomous cloud mission execution and production safety qualification require further implementation and independent tests.

## Where to find things

| Path | Contents |
| --- | --- |
| `GCSViews/`, `MainV2.cs` | Inherited Mission Planner flight-data UI and native connection handling |
| `plugins/`, `Plugins/AeroHelmCloud/` | Vihanga link, cloud UI and compiled plugin project |
| `services/vihanga_link/` | Pi UART publisher, private relay, identity verification and telemetry tests |
| `cloud/` | Separate fleet-control/analytics research code; **not** the live `:8443` relay |
| `scripts/` | Windows build, installation, launch and verification |
| `docs/` | Deployment, bench tests, architecture and historical engineering records |

See [architecture](docs/AEROHELM_PROJECT.md), [status](docs/AEROHELM_STATUS.md), [contributing](CONTRIBUTING.md) and [source attribution](docs/AEROHELM_PROVENANCE.md).

## Safety, license and attribution

This repository contains substantial code derived from ArduPilot Mission Planner and retains its [GPL-3.0 license](COPYING.txt) and original copyright notices. Vihanga is an engineering ground station, **not a certified flight-control system**. Use propellers-removed, disarmed test conditions for initial hardware commissioning, and retain an independently tested local pilot/failsafe path.
