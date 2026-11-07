# Vihanga GCS — merged master Windows acceptance

This procedure tests the **merged source** and does not authorize flight. The private
Vihanga relay is separate from the currently deployed Oracle M1 environment.
A GitHub merge does not install either a Pi UART process or an Oracle service.

## Windows update and exact-build verification

Use Windows PowerShell 5.1 and Visual Studio 2022/MSBuild. Save any local
changes outside the repository first; do not force a switch or reset. Close
all Mission Planner windows and any known legacy TCP bridge on port 5760.
Keep the last known-good runtime backup.

```powershell
Set-Location 'C:\Users\saita\AeroHelm-GCS'
git status --short
git fetch origin
git switch master
git pull --ff-only origin master
git submodule update --init --recursive

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install-VihangaVerified.ps1 -Apply
if ($LASTEXITCODE -ne 0) { throw 'Native build/install failed: do not launch.' }

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Verify
if ($LASTEXITCODE -ne 0) { throw 'Exact EXE/DLL verification failed.' }
```

Do not launch the previous executable if either step fails. The manifest
must match the current `master` commit and recorded EXE/plugin DLL SHA-256.

## Launch and bench observation

For an independent on-site USB/telemetry-radio comparison, use an explicitly
selected direct connection. Do not run an additional Pi process owning the
same serial port. Use a physically supervised, disarmed, propeller-free test
fixture and validated power/safety procedures.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 -LocalDirectBackup -NoInternalBridge -NoAutoTcp
```

For the **new private relay only after that service and Pi have actually been
provisioned**, launch a separate fresh session. The `LinkOrigin` argument
must be the exact, approved private Tailscale HTTPS origin. Do not paste
JWTs, device tokens or passwords into logs or issue comments.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 -NoAutoTcp -LinkOrigin 'https://YOUR-PRIVATE-RELAY.ts.net'
```

Within PATHS, authenticate to the new telemetry service. Observe the real
HEARTBEAT (0) and ATTITUDE (30) counters in TOOLS > Attitude monitor and
the native Mission Planner HUD. Independently compare incoming position,
battery, GPS and attitude with a direct connection. Verify stale state on
disconnection and absence of replay after reconnect. No cloud message
from this release may arm, mutate parameters or upload missions.

## Boundaries

* The Oracle M1 legacy URL must not be passed as the new relay origin unless
  it has separately been replaced with and validated against the new protocol.
* If Pi reports HB>0 and ATT=0, investigate actual FC stream configuration.
  Do not simulate attitude or bypass token/CRC checks.
* Offline Python tests, Windows MSBuild CI, GUI smoke and physical bench
  acceptance are independent gates. Record the exact git SHA and build hashes.
* Do not conduct physical autonomous flight from this test release.
