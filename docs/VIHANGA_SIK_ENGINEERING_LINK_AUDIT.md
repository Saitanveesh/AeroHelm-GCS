# Vihanga engineering-data-link vs. SiK — scoped audit (2026-11-09)

## Definition of the percentage

This is a **source implementation coverage score**, not a measured radio-quality percentage or a statement that today's connected drone has passed all flight/bench tests. Only the five requested engineering-data-link functions are scored. Physical arming, mode control, flight/mission actuation and RC autonomy are excluded. Each of 20 clearly enumerated binary criteria is worth 5 points, regardless of category. Criteria supported by existing or newly committed source earn 5 points; unexecuted physical/native acceptance criteria score zero. The criterion for real GUI acceptance is intentionally zero until a Windows machine demonstrates live data.

**Source coverage: 11 of 20 criteria = exactly 55%.** Real deployed/field functional equivalence is **not quantifiable** without on-device validation of the five transaction classes.

| Component | Passed / total | Points | Verified in repository | Missing before full parity |
| --- | --- | --- | --- | --- |
| 1. Bidirectional heartbeat | 2/4 | 10/20 | Genuine FC HEARTBEAT IDs are relayed; source freshness and rates are tracked | GCS-origin MAVLink HEARTBEAT to FC; physical FC receipt/timeout testing of reverse HB |
| 2. Attitude and GPS streaming | 3/4 | 15/20 | Real ATTITUDE ID30 with native yaw, GPS_RAW_INT ID24 and GLOBAL_POSITION_INT ID33 are relayed; CRC/coordinates checked; native downstream | Sustained physical stream-rate, age, GPS quality and reconnect acceptance in desktop |
| 3. Full parameter list acquisition | 3/4 | 15/20 | Fixed disarmed PARAM_REQUEST_LIST; indexed PARAM_VALUE collection plus bounded missing index requests; verified complete table exposed in desktop | Repeated session refresh and reliable recapture after FC changes or interrupted full scans (currently 1-per-session quota) |
| 4. PARAM_SET and calibration | 0/4 | 0/20 | A dormant isolated ParameterWriteTransaction source module exists but is not attached to the live relay/Pi nor the native UI | Authenticated PARAM_SET, bounded onboard execution, real PARAM_VALUE/readback reconciliation and separate calibration workflow |
| 5. Native Windows HUD and map | 3/4 | 15/20 | Authenticated WSS viewer to loopback 127.0.0.1:5760; native Mission Planner DATA HUD and map parser; explicit one-click entry plus GPS freshness | Actual Windows visual-smoke acceptance with moving pitch/roll/yaw and plotted GPS track |
| **Total** | **11/20** | **55/100** | **Source implemented; not flight-certified** | **9 discrete release criteria missing** |

The first three system-level score rows are source-protocol capabilities, not measured uptime. An actual SiK radio transparently carries many MAVLink operations outside this five-component scope; this 55% does not describe general-purpose MAVLink parity.

## New native Windows binding in this branch

1. `plugins/AeroHelmReceiveOnlyBridge.cs`: authenticates WSS viewer at `/ws/mavlink/view/{aircraft}` and relays original bytes into the single loopback TCP listener. MAVLink2/v1 CRC validation now covers GLOBAL_POSITION_INT ID33 (CRC_EXTRA=104, 28-byte payload), with geographical coordinate sanity checks and 5-second monotonic freshness tracking. Original frames, not synthesized coordinates or attitudes, go to the Mission Planner parser. All outbound native GCS MAVLink bytes remain **discarded**.
2. `plugins/AeroHelmCloud.cs`: adds `TOOLS -> Open cloud Flight Data (HUD + GPS map)` for the dedicated relay, and `TOOLS -> FC parameters (cloud read-only)`; no extra top-level toolbar button or competing HUD is introduced. PATHS status distinguishes HB+ATT from HB+ATT+GPS.
3. `plugins/AeroHelmLinkSelector.cs`: direct connection selection reuses `MainV2.doConnect(...,"TCP",...,false,false)` and native DATA map/HUD. Active USB/radio link is never silently disconnected.
4. `plugins/AeroHelmPlatformV2.cs`: adds a full verified parameter table view using the existing authenticated `GET /parameters/values`. It refuses missing, mismatched-count, stale or partial snapshots and never transmits a parameter write.
5. `scripts/Test-AeroHelmWindowsPages.ps1`: checks the above source binding, protocol whitelist and native raw-uplink discard.
6. `scripts/Start-VihangaEngineeringDataLink.ps1`: isolated Windows build with source/EXE/plugin SHA256 manifest, then explicit launch with pinned Oracle Compute instance identity and exact HTTPS :8443 origin.

## Isolated Windows build / launch — actual changed desktop code

Use a separate clean checkout, not your verified `master` Windows binary:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git fetch origin
git worktree add C:\Users\saita\Vihanga-EngineeringDataLink origin/feat/engineering-datalink-native-bind-20261109
cd C:\Users\saita\Vihanga-EngineeringDataLink
git switch -c feat/engineering-datalink-native-bind-20261109
$ocid = '<your-real-full-Oracle-Compute-instance-OCID>'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaEngineeringDataLink.ps1 `
    -LinkOrigin 'https://aerohelm-vcn.tailf3ab6a.ts.net:8443' `
    -OracleInstanceOcid $ocid -Build -Launch
```

Use the full OCID already configured for the running Oracle Compute instance, not an IP or a guessed instance name. If the indicated worktree/branch already exists, inspect it first instead of overwriting. Requires Visual Studio full .NET Framework MSBuild and a closed Mission Planner process. This deliberately builds into the separate source checkout and never touches the main verified master installation. It does not install or restart the running Pi/Oracle services.

Inside the application, choose `TOOLS > Open cloud Flight Data (HUD + GPS map)` and sign in with the dedicated relay telemetry credentials. Observe the native DATA HUD and map; `TOOLS > FC parameters (cloud read-only)` exposes the snapshot. PATHS retains the separate manual USB/telemetry-radio connection. A missing real GPS position is explicitly reported as missing, not painted on the map.

## Engineering link gaps — exact implementation requirements

- **Downlink heartbeat**: allowlist only genuine GCS HEARTBEAT from an authenticated registered GCS, bind source system/component and sequence; route to the Pi sole-UART writer, correlate the real FC-side effect/timeout and respect expiry and disconnection. A Pi transport echo is not an FC handshake.
- **Live frame acceptance**: controlled 10-minute Windows HUD/map run with measured HB/ATT/POS packet rates, MAX frame age, CRC rejection, yaw movement and GPS accuracy; include deliberate network outage and reboot handling. Record logs and screenshots.
- **Full parameter reliability**: robust repeated `PARAM_REQUEST_LIST`, resume/re-request missing indices, full name+count+type integrity, generation/FC restart isolation, and versioned cache. The present disarmed one-shot/partial recovery is not equivalent to Mission Planner's normal parameter lifecycle.
- **Parameter writes**: privileged, separately authorized disarmed `PARAM_SET`, exact parameter allowlist, type-aware encoding, one outstanding transaction, real `PARAM_VALUE` or explicit `PARAM_REQUEST_READ` verification and retry/reconciliation. ArduPilot may not spontaneously echo a new `PARAM_VALUE` after `PARAM_SET`; do not treat a transport ACK as write completion. Reconcile stored value and reject writes without readback.
- **Calibration**: separate supported `PREFLIGHT_CALIBRATION`/sensor-specific sessions with progress/status and safety state; `PARAM_SET` alone is not full compass/accelerometer/radio calibration.
- **Desktop GUI acceptance**: compile exact source and run native Flight Data, map, HUD and parameter screens with real FC packets. The installed release may have older binaries until this explicit build succeeds.

## Scoring is not a measured service-level agreement

The user's reported 286 ms latency is not independently reproduced by this GitHub source audit. Measure at least median, p95/p99 one-way command/telemetry age, jitter, loss/duplicate rates, reconnect recovery and GPS/HUD freshness before operational claims. When the nine missing criteria actually pass, this *scoped 20-criterion source-and-acceptance rubric* reaches 100%. It does not make an IP tunnel identical to a SiK radio's transparent unrestricted MAVLink link.

Standards: https://mavlink.io/en/services/parameter.html and https://github.com/mavlink/c_library_v2/blob/master/standard/mavlink_msg_global_position_int.h

**Release state:** native engineering-data-link source committed, physical Windows build and Pi/Oracle bench validation not performed by this repository edit. No cloud motor-control or flight actuation is enabled here.
