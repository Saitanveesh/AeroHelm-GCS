# Windows GCS session launcher repair — 2026-11-05

## Confirmed failure

The operator's verified Windows EXE/DLL build was **successful**.
The subsequent `Update-AeroHelmLatest.ps1 -Apply -Launch` command failed
before Mission Planner could start because
`scripts/Start-AeroHelm-Session.ps1` contained three copies of the
environment/legacy bridge/Touch invocation block spliced into the middle
of an unfinished process-name regular expression.

PowerShell reported:
- `Unexpected token 'AEROHELM_CLOUD_URL'...`
- `Missing closing '}'`
- `Unexpected token ')'`

No passwords, Pi services, cloud deployments, FC commands or installed
DLLs were implicated in the parse error.

## Corrective changes

- Replaced **the entire** session launcher with one complete flow:
  private HTTPS origin validation, process-local settings, bounded
  loopback-port ownership check, optional *explicitly approved*
  verified legacy Python bridge stop and a single Touch launch.
- No automatic unknown-process termination and no non-loopback port reuse.
  The old adapter is identified by executable name **and** exact local
  script path, and rechecked immediately before an operator-approved stop.
- `Test-AeroHelmWindowsPages.ps1` now uses the PowerShell AST parser
  (`System.Management.Automation.Language.Parser.ParseFile`) on all
  production launch/build scripts, and rejects duplicated session
  configuration blocks.
- `Update-AeroHelmLatest.ps1` runs those checks **before** rebuilding
  the native executable or replacing runtime files.
- The source-level Python suite verifies that the session contains exactly
  one Touch launch and one setup/optional legacy bridge flow.
- GitHub Actions path filters now include the session and repair scripts.

The GCS GitHub Actions workflow may be disabled/unavailable; don't claim
its AST parser checks passed in CI when no run is visible. A source check
is also not a successful Windows GUI/hardware acceptance test.

## Windows operator recovery

Close Mission Planner, use Windows PowerShell 5.1, and stay on the reviewed
branch. Preserve tracked edits rather than resetting or deleting them:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git status --short
git pull --ff-only origin feat/gcs-research-cybershield-twin-ui
if ($LASTEXITCODE -ne 0) { throw 'Git update failed; stop.' }
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
if ($LASTEXITCODE -ne 0) { throw 'PowerShell source checks failed; stop.' }
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-AeroHelmLatest.ps1 -Apply -Launch -StopVerifiedLegacyBridge
```

The updater backs up the old GCS, recompiles from the current source
commit, verifies the EXE and plugin SHA-256 and launches the latest
read-only workspace. The bridge-stop flag permits termination only after
verifying that TCP 5760 belongs to the old local AeroHelm Python adapter.
If it belongs to anything else, startup stops and the operator should
inspect the process rather than forcibly killing it.

This is a **Windows launcher repair**, not the separate Oracle M1 rollout
needed for genuine HEARTBEAT/ATTITUDE counters, nor a V3 physics twin or
Kerberos deployment.
