# AeroHelm Cloud — Mission Planner plugin, milestone 1

The original Mission Planner user interface, calibration workflows, firmware tools and MAVLink transport are **unchanged**.

This first plugin adds a **AeroHelm Cloud** menu item to Mission Planner that allows the operator to:

- Enter a private HTTPS AeroHelm Cloud origin (for example \`https://host.tailnet.ts.net\`).
- Authenticate with the existing AeroHelm Cloud operator account.
- List registered aircraft and their server-reported online/offline state.
- Open and close a **telemetry view session** for a fresh hardware aircraft.

**This does not install a MAVLink stream into Mission Planner.** The existing AeroHelm Cloud backend is a structured telemetry API; it is not a raw bidirectional MAVLink relay. A view session is not a Pixhawk connection or permission to fly. The desktop GCS's top-right native Connect button still uses the original serial/TCP/UDP connection modes.

## Source and loading

The plugin source is at \`plugins/AeroHelmCloud.cs\`. Mission Planner's existing \`PluginLoader\` discovers \`*.cs\` in the runtime \`plugins\` directory and compiles them on startup. Its C# source plugin mechanism is distinct from standard MSBuild inclusion: the main \`MissionPlanner.csproj\` excludes \`plugins/**\`.

After compiling Mission Planner, copy this plugin source to the folder containing the running \`MissionPlanner.exe\`, under \`plugins\AeroHelmCloud.cs\`. Do **not** run it directly from your cloned repository unless that is also the application's runtime plugins folder.

Check **Ctrl+P → Plugins** in the app for load errors. An AeroHelm Cloud menu entry should appear after the plugin loads. Keep the original Mission Planner connection intact.

## Build validation

A standalone project validates plugin source against the actual Mission Planner application, without modifying its project files:

    dotnet restore Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj
    dotnet build Plugins/AeroHelmCloud/AeroHelmCloud.Plugin.csproj -c Release --no-restore

This requires Windows .NET Framework 4.7.2 developer targeting pack and the dependencies required by upstream Mission Planner. A GitHub Actions workflow performs the same check on a Windows runner.

### Windows installation (PowerShell)

From a finished checkout:

    git fetch origin
    git switch --track origin/feat/aerohelm-cloud-plugin
    git submodule update --init --recursive

Build and run upstream Mission Planner from Visual Studio **before** installing the plugin. Identify the directory actually containing \`MissionPlanner.exe\` (do not guess Debug/Release).

Then copy the \`plugins/AeroHelmCloud.cs\` file into that executable's adjacent \`plugins\` folder, and restart the application.

## Server requirements and security

- AeroHelm Cloud must be reachable from Windows via a trusted private HTTPS URL.
- Access is via \`/api/v1/auth/login\`, \`/api/v1/vehicles\` and \`/api/v1/connections\`.
- Credentials and access token remain only in the transient dialog, not in Mission Planner settings or disk.
- All non-loopback URLs **must** use HTTPS. The plugin does not disable TLS certificate verification and disallows redirects for authenticated requests.
- Tailscale access control and cloud operator credentials are independent requirements.
- There is no flight control, no calibration RPC, no parameter writing, and no raw MAVLink tunneling in this milestone.

## Next engineering task

Implement a separate, versioned **AeroHelm MAVLink relay protocol** between Pi, server, and desktop, with per-aircraft exclusive authority and message freshness enforced onboard. Validate it on a propellers-removed Pixhawk and in SITL. After this, bind its stream to Mission Planner's existing transport interface, then test parameters and calibration behaviour end to end. Do not simply make the cloud API forward arbitrary commands from web clients.
