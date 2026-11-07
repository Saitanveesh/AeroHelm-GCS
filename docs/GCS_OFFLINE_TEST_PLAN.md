# AeroHelm GCS — no-flight-controller test plan

**Stage:** staged research branch `feat/gcs-research-cybershield-twin-ui`;
native Windows compilation and physical HUD testing are **not yet passed**.

## Immediately available software-only checks

From a **separate** Windows PowerShell session in the GCS repository root:

```powershell
cd C:\Users\saita\AeroHelm-GCS
python -m unittest -v Tools.test_aerohelm_offline_gcs
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Run-AeroHelmOfflineChecks.ps1
```

The runner performs a Python source-policy suite, compiles/tests the real
read-only C# WebSocket-to-TCP adapter using the .NET 8 SDK, and executes
offline survey geometry tests. For a source-only check where .NET 8 SDK is
not installed, add `-SkipDotNet`; this explicitly reduces coverage.

Python source checks cover:
- M1 cloud read-only TCP pins both external Python and integrated native
  adapters to `127.0.0.1:5760`; arbitrary TCP destinations are not selected
  by the AEROHELM_CLOUD_READONLY profile.
- Native `doConnect` suppresses parameter download for **all TCP entry
  points** when `AEROHELM_CLOUD_READONLY=1`, not merely the CONNECT UI handler.
- No cloud-originating MAVLink command output in the native receive-only
  WebSocket adapter, and existing physical USB/radio behavior stays intact.
- HOME status distinguishes generic frames from real heartbeat/attitude,
  including stale conditions. Roll/pitch labels stop presenting stale numbers.
- Custom toolbar icons and hidden upstream HELP/SIMULATION tiles.
- One-click FLIGHT opens native Mission Planner and requests loopback TCP;
  no unsanctioned telemetry fabrication or remote command authority.

C# tests compile the *actual* `AeroHelmReceiveOnlyBridge.cs` as a linked
source file and validate MAVLink1/v2 envelope parsing, roll/pitch degree
conversion, bad/NaN payload rejection and MAVLink2 incompatible flags.

**These are not equivalent to a successful Windows MissionPlanner.sln build.**
The full native solution requires Visual Studio's MSBuild.exe with the
.NET Framework 4.7.2 targeting pack, and a separate smoke test of the
compiled GCS + installed plugin. Preserve old working DLL until those pass.

## Avoid the existing five-minute Getting Params hang

The cloud Python bridge purposely discards *all* GCS-originating messages.
Therefore parameter fetching cannot finish via this path.

- **Do not** start raw `MissionPlanner.exe` on an unverified older build and
  assume it inherits the new source fix.
- In the reviewed development build, always launch using
  `scripts/Start-AeroHelm-Session.ps1`, which sets the read-only flag.
- The new `MainV2.doConnect` guards the actual TCP connection, preventing
  any parameter request made by other code from starting a read-only session.
- The GUI must use a trusted local receive-only bridge; do not connect to
  a generic TCP device in cloud read-only profile.
- If an older executable still says `Getting Params`, cancel promptly.
  The Windows compiled DLL/exe has not yet been proven to contain these fixes.

## What requires the user's presence and prop-off bench acceptance

- Native flight HUD receiving real Pixhawk HEARTBEAT and ATTITUDE, and
  gently tilting the physical disconnected-props FC to compare angles.
- Live private camera stability; cloud Tailscale and read-only telemetry.
- Operator login, token expiry/recovery and startup/reconnect states.
- Nine touch-panel navigation on the user's native Windows installation.
- Calibration/mission activation remain **disabled** for cloud read-only GCS.

No present test writes parameters, arms, uploads a mission or opens a second
Pi UART reader. No Pi/Oracle/FC service is restarted by repository changes.
