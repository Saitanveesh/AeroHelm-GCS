# Vihanga source provenance and license

The ground station is a derivative of
[ArduPilot Mission Planner](https://github.com/ArduPilot/MissionPlanner).
Mission Planner's UI, MAVLink support, flight planning and local telemetry
stack remain upstream work; this repository adds private-cloud inspection,
read-only telemetry plumbing and Vihanga operator modules.

The product name `Vihanga GCS` is the user-facing label. Existing
`AeroHelm` namespaces, environment variables, launch-script aliases and
plugin DLL names are retained to preserve upgrade compatibility, not to
claim authorship of upstream Mission Planner code.

Preserve third-party notices, copyright notices, original licenses and
source availability requirements under `COPYING.txt` and project
dependencies. Product-facing branding does not replace or supersede those
obligations. Review dependencies and redistributed binaries before
publishing a release.

The Phase-3 native toolbar is maintained in
`feat/vihanga-phase3-native-toolbar` and requires exact-build Windows
validation and an independent, authorized physical acceptance process.
