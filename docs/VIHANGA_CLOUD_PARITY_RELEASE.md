# Vihanga cloud parity — implementation and deployment gates

**Scope:** Actual Pixhawk → Raspberry Pi 5 → Oracle private relay → Windows Mission Planner.  
**Release state:** Source implementation; **no live aircraft or Windows acceptance has been performed for this revision**.

## What this change implements

| Feature | Code path | Safety boundary |
| --- | --- | --- |
| Recover missing telemetry after FC restart | `pi_agent.py` recognizes a real FC heartbeat gap, resets previous stream evidence, then makes up to two fixed rate requests | Requires explicit `VIHANGA_DISARMED_STREAM_RECOVERY=1`, fresh disarmed ArduPilot heartbeat; never after any armed heartbeat seen during that agent process |
| Complete read-only parameter capture | Windows Parameters panel → signed-in `POST .../parameters/refresh` → Oracle → Pi → `PARAM_REQUEST_LIST`; Pi may request up to 32 missing indices using `PARAM_REQUEST_READ` | Independent opt-in at both Pi and Oracle; one remote capture per Pi/relay session; current FC must be disarmed; no PARAM_SET |
| Parameter integrity | Oracle accepts real CRC-checked `PARAM_VALUE`, validates index/count, reports only a complete/fresh snapshot as complete | Missing values remain incomplete; numeric integer decoding is enabled only with a real ArduPilot heartbeat |
| GNSS and map state | Relay `position_quality` identifies actual 3D fix, valid nonzero coordinates, and separate HOME_POSITION; native map rejects invalid automatic recentering | No invented GPS coordinates, no inferred home |
| Local backup | Existing Windows native USB/radio selection remains unchanged | Cloud read-only bridge continues to discard outgoing flight commands |

**Not delivered:** general cloud calibration commands, PARAM_SET, native Mission Planner CONFIG parameter protocol parity, mission upload/execute, guided movement, arm/disarm or takeoff. Those require a separately reviewed physical command channel and aircraft acceptance. Code named `supervised_control.py` is currently simulator-only.

## Existing-machine rollout: no automatic deployment

**Do not copy a fresh Git checkout into an active Pi UART-owning process or assume GitHub changes are deployed.** The Windows GCS uses an exact Git-SHA/EXE/DLL manifest; pulling this source on Windows requires a backed-up rebuild and verification.

For supervised, propellers-removed, disarmed bench tests, the Pi may be configured with:

```ini
# /etc/vihanga/pi.env — add this line if absent
VIHANGA_DISARMED_STREAM_RECOVERY=1
# Local automatic one-shot capture at startup, plus remote disarmed capture consent.
VIHANGA_DISARMED_PARAM_SNAPSHOT=1
# Separate older on-demand VOLATILE stream probe can remain disabled:
VIHANGA_DISARMED_STREAM_PROBE=0
```

The Oracle relay requires its own independent operator permission:

```ini
# /etc/vihanga/relay.env — add this line if absent
VIHANGA_ALLOW_DISARMED_PARAM_REQUEST=1
```

These flags **default to 0**. Existing secret files, device credentials, Oracle identity pinning and the old HTTPS `:443` service must be preserved. Use `sudoedit` on the correct machine and confirm each key appears only once. After an approved maintenance change, restart **only** the affected Vihanga service on that same machine. The Pi remains the sole owner of the FC UART; do not restart the old `aerohelm-agent@` process.

To enable telemetry-only recovery without authorizing a parameter capture, set only `VIHANGA_DISARMED_STREAM_RECOVERY=1` and leave the other maintenance flags disabled.

## What to verify after rollout

1. **FC reboot:** with the aircraft disarmed and propellers removed, power cycle the FC while keeping the Pi running. After the genuine heartbeat returns, the Pi should log one bounded `MAV_CMD_SET_MESSAGE_INTERVAL` request set and receive real ID 30/33/74 again. Never interpret a link ping as an FC ACK.
2. **Parameter capture:** with both explicit maintenance flags enabled, use the existing PARAMETERS panel **Request disarmed read-only parameter capture** button. `QUEUED` is not completion. Check `GET /api/v1/vehicles/{id}/parameters` through an authenticated operator session until `verified_complete=true`. Missing indices remain partial. Physical parameter writes are not supported through this path.
3. **GPS:** call `GET /api/v1/mavlink/health`. The `position_quality` fields `gps_3d_fix`, `map_position_valid` and `home_position_verified` are independent. Indoors, the last two may legitimately remain false. Do not treat a zero-coordinate map viewport as aircraft position.
4. **Windows:** on a fresh, verified build select PATHS → Cloud telemetry. Check actual HUD movement and reconnect/staleness. The native SETUP/CONFIG pages still require their full command/parameter protocol and are not released for cloud operation.

## Windows build / test

```powershell
cd C:\path\to\Vihanga-GCS
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
dotnet run --project .\Tools\AeroHelmMavlinkBridgeTests\AeroHelmMavlinkBridgeTests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Repair-AeroHelmWindows.ps1 -Apply
```

The last command performs the transactional native build and verifies the installed executable/plugin manifest. Stop if any stage fails; do not manually modify the release manifest. The new repository name is `Saitanveesh/Vihanga-GCS`; existing local folder and compatibility script names can remain unchanged.

## Persistent vs self-recovering streams

This implementation **does not write persistent FC stream-rate parameters**. Instead it restores fixed, nonpersistent rates during a future disarmed heartbeat session when explicitly allowed. Persistent `SRx_*` configuration requires finding the actual ArduPilot MAVLink stream index for the wired serial port and verifying it locally; `SRx` is not interchangeable with `SERIALx`. Do not write guessed FC settings.

Reference protocol:
- [ArduPilot telemetry port and SRx rate setup](https://ardupilot.org/plane/docs/common-telemetry-port-setup.html)
- [ArduPilot parameter retrieval and missing index requests](https://ardupilot.org/dev/docs/mavlink-get-set-params.html)

## Acceptance still required

Offline tests and Windows compilation must pass, followed by supervised disarmed bench tests for UART ownership, FC reboot recovery, bounded parameter completeness, GNSS validity, disconnected-viewer freshness and independent pilot backup. Airborne command authority remains disabled.
