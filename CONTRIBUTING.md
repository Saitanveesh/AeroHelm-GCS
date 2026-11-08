# Contributing to Vihanga GCS

This is a Mission Planner-derived Windows application with a separate Raspberry Pi / Oracle private telemetry service. Changes must preserve the upstream build, local pilot recovery paths and the distinction between observation and aircraft control.

## Before editing

1. Identify which component owns the behavior: native Mission Planner (`MainV2.cs`, `GCSViews/`), the Vihanga plugin (`plugins/`), Pi/Oracle transport (`services/vihanga_link/`) or research cloud services (`cloud/`).
2. Read [architecture](docs/AEROHELM_PROJECT.md), [verified status](docs/AEROHELM_STATUS.md) and the relevant deployment guide.
3. Work in a topic branch. Avoid force-pushing `master`, rewriting release history or deleting upstream dependencies because their names look old.
4. Do not commit API keys, tokens, passwords, local environment files, build backups or flight logs containing private location data.

## Code and UI standards

- Use specific technical names. Prefer **“MAVLink heartbeat received”** to **“connection healthy”** when attitude and position have not been checked.
- Show `STALE`, `UNAVAILABLE` or the actual error when data is missing. Never invent attitude, GPS, sensor states or command acknowledgments to keep the interface populated.
- Keep user-facing text short and operational. Avoid promotional slogans, placeholder status narratives, duplicate controls and non-actionable status labels.
- Preserve the ability to use the local USB/radio pilot connection independently of cloud telemetry.
- Use explicit timeouts, bounded queues, message identity checks and fail-closed authorization on device and operator paths.
- Never treat a link-level acknowledgment or an HTTP response as a physical flight-controller execution acknowledgment.
- Leave **physical flight commands disabled** in the current private telemetry service. Any future command path requires a separate reviewed design and its own safety, HIL/SITL and bench acceptance.

## Test before merging

On Windows, close Mission Planner, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
dotnet run --project .\Tools\AeroHelmMavlinkBridgeTests\AeroHelmMavlinkBridgeTests.csproj --configuration Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Repair-AeroHelmWindows.ps1 -Apply
```

The repair script performs native build, plugin installation and exact EXE/DLL verification. A test on source files is **not** proof that Windows loaded the new executable. Do not manually change `build-logs/aerohelm-consolidated-release.json` to make a stale build pass.

For the Pi/Oracle service, run relevant offline Python tests using `services/vihanga_link/requirements-test.txt` and the project test conventions. Do not connect tests to a flight controller unless the procedure explicitly requires an on-site disarmed bench. Document any real hardware results with the machine, measured message IDs, rates, time window and failure cases.

## Documentation standards

Each feature document should state:

- **What it does** and which machine runs it.
- **What has been verified** versus what is planned or simulated.
- **Exact prerequisites**, command location, expected output and rollback.
- **Safety boundary:** can it read telemetry, request fixed diagnostics or physically command the FC?

Use plain language. Define terms once; avoid unexplained acronyms and generic claims of completeness. Keep old incident details in Git history instead of repeatedly duplicating them in current setup guides.

## License and provenance

Retain the original Mission Planner / ArduPilot copyright notices and GPL-3.0 obligations. See [COPYING.txt](COPYING.txt) and [project provenance](docs/AEROHELM_PROVENANCE.md). Do not remove vendored libraries, platform assets, data or localization files without first proving that they are unused by the Windows solution and other supported targets.
