# GitHub repository description and topics

**Project name:** Vihanga GCS  
**Repository path:** `Saitanveesh/AeroHelm-GCS` (retained for source/build compatibility)  
**Code license:** GPL-3.0

## About description

Use this text in GitHub's repository **About** field:

> Vihanga GCS: Mission Planner-based ground station with private Pixhawk telemetry via Raspberry Pi, Oracle Cloud and Tailscale, plus an independent pilot link.

This is deliberately narrow. It describes a real, tested telemetry capability without claiming remote flight command authority, autonomous flight or operational certification.

**Suggested topics:** `ardupilot`, `mission-planner`, `ground-control-station`, `mavlink`, `pixhawk`, `raspberry-pi`, `tailscale`, `telemetry`, `uav`.

**Website / URL:** use this repository or an actual maintained project documentation site. Do not link an unverified product landing page.

## How to update the About field

1. Open [the repository](https://github.com/Saitanveesh/AeroHelm-GCS).
2. On the repository Code page, select the gear icon beside **About**.
3. Replace the description with the text above, set topics, and save.

This file is the reviewed **proposed metadata**. Editing Markdown files does **not** change GitHub's About description automatically; that repository setting must be saved separately.

## Public claims to avoid

- Do not describe physical cloud arming, mission upload or takeoff as shipped.
- Do not describe simulator approval or private WebSocket ping as aircraft command execution.
- Do not claim multi-aircraft production operation from a single-aircraft bench result.
- Do not claim a guaranteed link latency from the observed 286 ms diagnostic.
- Do not use the original upstream Mission Planner project description as if it described the Vihanga fork.

For actual results, see [project status](AEROHELM_STATUS.md); for setup, see [README](../README.md).
