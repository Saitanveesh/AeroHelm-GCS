# Mission Planner upstream build reference

VIHANGA is derived from the ArduPilot Mission Planner Windows/.NET source.
The upstream build process, dependencies and licensing remain relevant.

- [Mission Planner upstream source](https://github.com/ArduPilot/MissionPlanner)
- [Windows source build guide](https://ardupilot.org/planner/docs/mission-planner-installation.html)
- [Source license](https://github.com/ArduPilot/MissionPlanner/blob/master/COPYING.txt)

Native Windows build preparation requires Visual Studio with the C#/.NET
desktop workload and the restored packages/submodules for the pinned
Mission Planner source. Review the repository's `vs2022.vsconfig` and
`MissionPlanner.sln`; do not assume a plugin-only build certifies the
full application.

For this derivative, use `scripts/Build-Integrated-AeroHelm.ps1` through
`scripts/Consolidate-AeroHelmWindows.ps1` for backed-up upgrades.
The `plugins/VihangaSharedUi.cs` source is compiled only into the
plugin project, not the MissionPlanner project.
