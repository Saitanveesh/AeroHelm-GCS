# AeroHelm GCS

**Cloud-first UAV fleet coordination and ground operations.**

AeroHelm is a ground-control engineering project exploring how one operator can coordinate multiple aircraft through a private cloud service without making the cloud responsible for onboard flight safety.

The project combines fleet state, secure telemetry, mission records and a lightweight ground interface. The design also retains a separate physical telemetry or USB connection for an on-site pilot when the cloud path is unavailable.

## The problem

A field operator needs to know which aircraft is connected, when each telemetry sample was received, who currently holds operational authority and whether a mission was approved. These questions become harder as the number of aircraft grows.

AeroHelm divides responsibility between the aircraft, the companion computer, cloud coordination and the ground station:

| Part | Responsibility |
|---|---|
| Flight controller | Stabilization, onboard failsafes and actual aircraft control |
| Companion computer | Sole ownership of the flight-controller serial link and collection of observed telemetry |
| Private network | Authenticated uplink and explicit stale-data handling |
| Cloud service | Fleet identity, mission records, independent leases, virtual workflow state and audit |
| Ground interface | Fleet overview, connection status, mission review and operator workflows |
| Direct pilot link | Independent USB or telemetry-radio connection, explicitly selected by the pilot |

## System layout

This is the **intended architecture across development branches**, not a diagram of a single certified deployment.

~~~text
  AIRCRAFT
  Flight controller
        |
        | MAVLink telemetry
        v
  Companion computer
  (single serial-port owner)
        |
        | Authenticated private uplink
        v
  CLOUD
  Vehicle identities / tenant scope
  Fleet state / mission records / leases
  Virtual workflow dispatch / audit
        |
        | API and fresh telemetry state
        v
  GROUND INTERFACE
  Operator view / link diagnostics / fleet status


  INDEPENDENT PILOT BACKUP
  Local pilot <-- USB or telemetry radio --> Flight controller
  (manual selection, separate connection)
~~~

An HTTP success response, a virtual-job acknowledgement and a physical flight-controller acknowledgement are different signals. One cannot be substituted for another.

## Engineering work

| Workstream | Where to inspect it | Current limitation |
|---|---|---|
| Native ground-station foundation | master branch | Inherited desktop application, not the completed cloud platform |
| Fleet API and mission coordination | [Cloud R1, draft PR #24](https://github.com/Saitanveesh/AeroHelm-GCS/pull/24) | Virtual-only dispatch; no aircraft commands |
| Telemetry freshness and sessions | [Cloud R1 source](https://github.com/Saitanveesh/AeroHelm-GCS/tree/feat/cloud-fleet-control-plane-r1/cloud) | Integration and deployment acceptance still required |
| Ground UI, cloud HUD and link handling | [Desktop integration branches](https://github.com/Saitanveesh/AeroHelm-GCS/pulls) | Source experiments are not equivalent to a tested installed executable |
| Cloud analytics offload | [Analytics development branch](https://github.com/Saitanveesh/AeroHelm-GCS/tree/feat/cloud-analytics-offload-r1) | Experimental, not a flight-control loop |
| Direct telemetry backup | [Dual-path design](https://github.com/Saitanveesh/AeroHelm-GCS/blob/feat/aerohelm-duplex-link-console/docs/DUAL_PATHS_CLOUD_HUD_RELEASE_PLAN.md) | Requires testing with a separately wired pilot connection |

The engineering focus is to keep aircraft identity, operator authorization, telemetry freshness, command authority and recovery independent. Each aircraft needs separate credentials and state. A cloud disconnect must not lead to automatic replay of uncertain aircraft commands.

[Detailed implementation and verification status](docs/AEROHELM_STATUS.md)

## Build

The default branch still contains the native Windows desktop foundation. Existing solution and executable filenames are retained for compatibility.

Use Visual Studio 2022 and the configuration in [vs2022.vsconfig](vs2022.vsconfig).

~~~powershell
git clone https://github.com/Saitanveesh/AeroHelm-GCS.git
cd AeroHelm-GCS
git submodule update --init --recursive
# Open MissionPlanner.sln in Visual Studio 2022
~~~

The separate cloud-service implementation lives in a development branch. Follow the [Cloud R1 README](https://github.com/Saitanveesh/AeroHelm-GCS/blob/feat/cloud-fleet-control-plane-r1/cloud/README.md) and [acceptance runbook](https://github.com/Saitanveesh/AeroHelm-GCS/blob/feat/cloud-fleet-control-plane-r1/cloud/ACCEPTANCE_RUNBOOK.md) to inspect that work. It is not authorized for physical flight commands.

## Technical documentation

- [Project architecture and design decisions](docs/AEROHELM_PROJECT.md)
- [Current status and acceptance boundaries](docs/AEROHELM_STATUS.md)
- [Cloud release and safety gates](https://github.com/Saitanveesh/AeroHelm-GCS/blob/feat/cloud-fleet-control-plane-r1/docs/CLOUD_CONTROL_PLANE_RELEASE_GATES.md)
- [Ground-station cloud integration design](https://github.com/Saitanveesh/AeroHelm-GCS/blob/feat/aerohelm-duplex-link-console/docs/AEROHELM_FULL_CLOUD_GCS_RFC.md)
- [Original native build documentation](docs/UPSTREAM_BUILD_REFERENCE.md)
- [Copyright, license and source attribution](docs/AEROHELM_PROVENANCE.md)

## Safety and licensing

**AeroHelm is an engineering research project, not a flight-certified multi-UAV system.** The Cloud R1 draft does not arm vehicles, upload physical missions or transmit arbitrary flight-control commands. Hardware acceptance needs independently verified telemetry, onboard failsafes, pilot recovery, test records and qualified sign-off.

The native application includes substantial work derived from [ArduPilot Mission Planner](https://github.com/ArduPilot/MissionPlanner), distributed under GPL-3.0 (see [COPYING.txt](COPYING.txt)). AeroHelm-specific work is described separately. Copyright and redistribution obligations are retained.

**There are no marketing illustrations in this README.** Actual photographs and software screenshots should be added only when they come from a verified hardware setup or known build.
