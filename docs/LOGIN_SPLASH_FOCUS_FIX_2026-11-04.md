# AeroHelm GCS — login disappearing behind Mission Planner splash

## Diagnosis

The reported Windows screenshot shows the native Mission Planner 1.3.83
splash and the standalone console, with the operator saying the password
dialog appears and then disappears when clicked.

The GCS source confirms Mission Planner's `Splash.Designer.cs` sets
`TopMost=true`, while the AeroHelm startup gate originally opened an
**unowned, ordinary** `ShowDialog()`. This is a Windows z-order/focus
problem: a topmost splash can obscure the credential dialog. It is not
evidence that the password is invalid or that a cloud login completed.

## Code fix

- In `Program.cs`, hide the native splash and clear its TopMost flag
  **before** cloud authentication begins. Only show it again after the
  modal returns success, then start `MainV2`.
- In `AeroHelmStartupGate.cs`, keep sign-in keyboard-accessible,
  show its own taskbar entry, and focus the password input on `Shown`.
- The existing cloud private HTTPS-origin validation, password clearing,
  JWT in-memory handling and explicit offline maintenance bypass are
  unchanged. No cloud or Pixhawk flight authority is added.
- Static Python and PowerShell regression checks assert ordering and focus.

## Deploy the code fix to Windows

Close Mission Planner normally (and any old Python TCP bridge holding port
5760 if switching to the embedded bridge). At the repository root:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git status --short
git pull --ff-only origin feat/gcs-research-cybershield-twin-ui
python -m unittest -v Tools.test_aerohelm_offline_gcs
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Repair-AeroHelmWindows.ps1 -Apply
```

Do not run through Git conflicts or uncommitted changes. The repair makes
a full prior-runtime backup and checks EXE and plugin fingerprints before
starting the updated GCS:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-Session.ps1
```

Expected: login stays **visible in front** until successful sign-in or
explicit Exit, then Mission Planner loads and the nine-tile touch
workspace becomes available.

Native Windows runtime verification is still pending an operator-run build
and UI smoke test; source-level checks are not a substitute.
