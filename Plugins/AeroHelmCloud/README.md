# Vihanga Windows plugin

This project builds the native Windows plug-in used by **Vihanga GCS**, the operator-facing name for the Mission Planner-based application. Source folders and assembly identifiers retain `AeroHelm` for compatibility.

The plugin implements the private cloud viewer, link selection, on-screen diagnostics and related Vihanga workspaces. It does **not** authorize general cloud aircraft control.

## How it is installed

**Do not copy `plugins/AeroHelmCloud.cs` into the runtime as a second source plugin.** Vihanga uses a compiled DLL and refuses duplicate source/DLL installations.

After closing Mission Planner, run this from the repository root in Windows PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Repair-AeroHelmWindows.ps1 -Apply
```

That process runs the offline source and MAVLink bridge checks, builds Mission Planner, installs the compiled plug-in, and verifies the release. The installed plug-in file is:

```text
bin\Release\net461\plugins\AeroHelm.Cloud.Plugin.Validation.dll
```

The launcher checks that the file exists, that an obsolete source plug-in is not installed beside it, and that the recorded EXE/DLL hashes match the exact Git revision.

## Start the private cloud viewer

From a **verified** Windows installation, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 `
  -NoAutoTcp `
  -LinkOrigin "https://YOUR-TAILSCALE-HOST.ts.net:8443" `
  -OracleInstanceOcid "YOUR-FULL-OCI-COMPUTE-INSTANCE-OCID"
```

In the GCS, open **PATHS → Cloud telemetry** and sign in with the independent Vihanga relay account. The private relay runs on Oracle HTTPS `:8443`, not the older AeroHelm API on `:443`.

The native DATA page uses genuine physical MAVLink packets. The cloud bridge is receive-only for aircraft control. Physical USB/radio operation remains a separate pilot-selected link.

## Components

- `plugins/AeroHelmCloud.cs`: plug-in entry point and consolidated toolbar.
- `plugins/AeroHelmLinkSelector.cs`: cloud versus independent pilot connection selection.
- `plugins/VihangaLinkSession.cs` and `VihangaLinkLoginWindow.cs`: dedicated relay identity verification and short-lived operator authentication.
- `plugins/AeroHelmReceiveOnlyBridge.cs` and `AeroHelmLinkQuality.cs`: read-only MAVLink forwarding, frame validation and freshness.
- `plugins/AeroHelmCloudAttitudeWindow.cs`: independent attitude diagnostics.
- `Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj`: compiled plug-in project.

See [Windows deployment](../../docs/VIHANGA_WINDOWS_BENCH_UPGRADE.md), [verified project status](../../docs/AEROHELM_STATUS.md) and [Pi–Oracle live-link setup](../../docs/VIHANGA_ORACLE_PI_LIVE_LINK_DEPLOY.md).

## Development and safety limits

The plugin project targets .NET Framework 4.7.2. The separate offline MAVLink bridge tests target .NET 8; the shared session code accounts for those different frameworks. Running unit tests does not replace a real Windows UI or Pixhawk acceptance test.

This software must never invent flight-controller telemetry, silently transfer pilot authority, or infer a physical command acknowledgment from an HTTP or WebSocket success. Cloud arm, takeoff, mission upload and arbitrary parameter writes are **not enabled** by this plugin.
