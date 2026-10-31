# AeroHelm Cloud plugin — reliable installation on Windows

Mission Planner's \`PluginLoader\` compiles C# source plugins from the runtime \`plugins\` folder **asynchronously**. An MSBuild success does not guarantee that a source plugin loads. It can also be hidden if a menu item is appended after the right-aligned connection controls.

**This branch now uses a compiled DLL deployment and an obvious \`CLOUD\` toolbar button immediately after \`HELP\`.** This is still only a cloud registry / viewing-session plugin, not a MAVLink transport.

## Install into your existing successful Mission Planner build

1. Close Mission Planner entirely.
2. Open PowerShell in \`C:\Users\saita\AeroHelm-GCS\`.
3. Run:

    git switch feat/aerohelm-cloud-plugin
    git pull --ff-only origin feat/aerohelm-cloud-plugin
    powershell -ExecutionPolicy Bypass -File .\scripts\Install-AeroHelmCloud.ps1

4. Start the application:

    .\bin\Release\net461\MissionPlanner.exe

The script builds \`Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj\` using Visual Studio's framework MSBuild, locates \`AeroHelm.Cloud.Plugin.Validation.dll\`, copies it into \`bin\Release\net461\plugins\`, and removes the old *runtime copy* of \`AeroHelmCloud.cs\` to prevent a duplicate plugin. It does not delete the version-controlled source or alter any original Mission Planner files.

## If CLOUD is missing

Press \`Ctrl+P\` to open the Plugins window. Check for **AeroHelm Cloud**. If absent, select **Errors** and inspect any compilation/runtime messages. The plugin loader is located at \`Plugin/PluginLoader.cs\`. If it shows as disabled, enable it and restart Mission Planner.

Check exactly what is installed:

    Get-ChildItem .\bin\Release\net461\plugins\AeroHelm*

The installed plugin must be the .dll; \`AeroHelmCloud.cs\` should no longer be in that runtime folder.

## What Connect means here

The plugin signs in to the existing AeroHelm Cloud API, lists reported aircraft, and opens a telemetry **view** session. It does not feed raw MAVLink messages into Mission Planner's COM connection or perform calibration. Cloud-based calibration requires the future bidirectional MAVLink transport, device/operator authorization controls, and bench acceptance tests.

## Security notes

- HTTPS is required for remote endpoints; never disable certificate verification.
- Operator credentials and access tokens are transient; do not paste either into command output.
- Do not make the cloud API publicly accessible. Use your private Tailscale network.
- Do not test command/calibration capabilities on a propeller-equipped aircraft.

## Current AeroHelm UI adjustments

- The plugin temporarily hides the upstream **Simulation** and **Help** toolbar buttons when loaded and restores their previous visibility on plugin unload. It does not modify Mission Planner's underlying command implementations or internal navigation state.
- The Cloud dialog uses the original Mission Planner-style WinForms layout and inherits Mission Planner's own theme, including its conventional controls. The separately styled dark UI has been reverted at the user's request.
- The original local serial/TCP/UDP Connect action is unchanged. The cloud dialog's **Connect view** remains non-flight-control only.

To apply changes, close the running GCS, pull the branch, rerun the installer, and restart the executable. No full-solution rebuild should be necessary if the base Mission Planner build is still valid and the plugin validation build passes.
