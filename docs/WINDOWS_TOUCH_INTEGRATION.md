# AeroHelm — Windows touch integration release gate

## Scope (Windows first)

This branch consolidates the two unmerged Windows touch proposals:

- Source foundation: `feat/aerohelm-touch-console` (native Mission Planner toolbar
  integration, vector-drawn nine tile icons, existing operations dialogs).
- Layout requirement from `feat/aerohelm-touch-workspace`: full-screen and
  exactly three columns by three rows. No font-dependent decorative glyphs.
- New working branch: `feat/aerohelm-windows-integration`.

The requested order is fixed:

| FLIGHT | PLAN | VISION |
| --- | --- | --- |
| OPS | SECURITY | FLEET |
| LINK | ANALYTICS | SETTINGS |

The black background, charcoal rounded tiles and centered white line icons are
deliberate. Standard Windows rendering must never substitute icon-font symbols.
The native Mission Planner main window remains underneath the full-screen HOME
modal; ESC and both top controls return to the original GCS.

## Module mapping

| Tile | Opens |
| --- | --- |
| FLIGHT | Native Mission Planner Flight Data |
| PLAN | Native Mission Planner Flight Planner |
| VISION | Existing private camera viewer launcher |
| OPS | Existing simulated operations and supervised maintenance panel |
| SECURITY | Existing AeroHelm INTEL security tab |
| FLEET | Existing AeroHelm INTEL fleet tab |
| LINK | Existing cloud telemetry/connection diagnostic panel |
| ANALYTICS | Existing INTEL history/report tab |
| SETTINGS | AeroHelm settings panel offering native Config/Tuning and cloud session |

None of the touch tiles adds a MAVLink command or changes aircraft authority.
Native Mission Planner retains direct COM/radio/USB flight-control access; cloud
telemetry and cached parameters do **not** authorize physical flight operations.
Supervised calibration is separately locally gated and disabled by default.

## Windows build (no aircraft needed)

Use an isolated checkout and ensure work is committed or stashed:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git status --short
git fetch origin
git switch -c feat/aerohelm-windows-integration --track origin/feat/aerohelm-windows-integration
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Integrated-AeroHelm.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Touch.ps1
```

If the branch already exists, use `git switch feat/aerohelm-windows-integration`
and `git pull --ff-only` instead of creating it again. The build requires
Visual Studio full-framework MSBuild and the .NET Framework targeting packs.
Do not overwrite a previously working Mission Planner installation if a build
fails.

For manual startup, run `Start-AeroHelm-Touch.ps1 -ManualTouch` and click
the **TOUCH** toolbar item. The default launcher sets
`AEROHELM_TOUCH_STARTUP=1`, opening the full-screen grid after regular
Mission Planner startup/authentication.

## Acceptance checklist

- [ ] Windows full Mission Planner solution builds successfully.
- [ ] AeroHelm compiled plugin builds/installs and loads without a duplicate
      source plugin, startup exception or hidden native controls.
- [ ] 3x3 tile order, rounded charcoal geometry, icons, focus indicators and
      caption rendering match the supplied reference at 1920x1080 and 1280x720.
- [ ] Pointer, keyboard Tab/Enter/ESC and Windows touchscreen work without
      clipped captions at 100%, 125% and 150% display scaling.
- [ ] FLIGHT and PLAN switch to actual Mission Planner native panels.
- [ ] VISION, OPS, SECURITY, FLEET, LINK, ANALYTICS, SETTINGS open the correct
      existing windows, can close, and can reopen without leaked modal windows.
- [ ] CLOUD operator login, regular Mission Planner local connection controls,
      recovery UI and existing backup executable remain functional.
- [ ] Network unavailable: LINK visibly reports stale/disconnected status;
      no stale data or cached parameter is represented as live aircraft state.
- [ ] Cloud API is unable to submit flight arming, mission upload or parameter
      writes as a consequence of a tile action.
- [ ] Bench test of any Pi/Pixhawk integration is run separately with motors
      disarmed and propellers removed; it is NOT implied by a successful UI build.

## Integration decision

Do not merge directly to `master` or deploy to the existing aircraft/Oracle
installation on source review alone. The Windows UI integration is source-ready
for an isolated review; it remains release-blocked until MSBuild, runtime smoke
tests and the relevant bench gates pass.

iOS/Android PWA branches are preserved without promotion in this milestone.
