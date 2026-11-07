# Vihanga GCS — bench candidate and source inventory

The latest integrated **Windows source** is the preserved branch
[`feat/vihanga-phase3-native-toolbar`](https://github.com/Saitanveesh/AeroHelm-GCS/tree/feat/vihanga-phase3-native-toolbar).
The independent cloud R2 staging backend is on
[`feat/cloud-oci-private-edge-r2`](https://github.com/Saitanveesh/AeroHelm-GCS/tree/feat/cloud-oci-private-edge-r2).

Windows candidate source when this index was written: `99cb54fdbc3e6e08e0b1f97ffc1635d2e97cd4b5`.
Cloud R2 candidate source when this index was written: `bc2ad0b1ea1c0aa7dba05f835442b24af01b7343`.

These changes are **not merged into master** as a tested runtime; the
original Mission Planner-derived default branch remains separate.
The closed draft PR chains remain accessible in GitHub history, and their
source is retained in branches/archived refs.

For exact Windows PowerShell commands, use:
[backed-up upgrade, cleanup and passive Pi/FC bench checklist](https://github.com/Saitanveesh/AeroHelm-GCS/blob/feat/vihanga-phase3-native-toolbar/docs/VIHANGA_WINDOWS_BENCH_UPGRADE.md).

That procedure does not delete operator settings, automatically connect
native radio, arm a vehicle, upload a mission or write parameters.
The installer backs up the last working runtime and verifies EXE/DLL
SHA-256 before removing known obsolete plugin copies. Generated output
cleanup is opt-in and separate.

## Certification boundary

A compiled Windows application, a GitHub commit, or a powered Raspberry
Pi/flight controller is **not** operational flight certification.
Before actual aircraft use: successful exact-commit Windows build,
plugin/UI smoke, authenticated private backend M1/R2 compatibility,
fresh Pi/FC telemetry, propeller-free direct-radio/failsafe bench evidence,
and independent approval are required. No automatic cloud-to-radio
control authority transfer exists.

See [certification criteria](https://github.com/Saitanveesh/AeroHelm-GCS/blob/feat/vihanga-phase3-native-toolbar/docs/VIHANGA_CERTIFICATION.md).
