# Vihanga operational acceptance — verification checklist

This is a release-gating procedure, not a declaration of airworthiness.
Software-only tests cannot establish that a physical aircraft, RC system,
radio, cloud service, firewall, geofence or failsafe is safe to fly.

## Automated gates

Use the exact reviewed GCS branch and a clean checkout. On the Windows
test station, close Mission Planner before rebuilding. Verify the
transactional installation:

```powershell
git switch feat/vihanga-phase3-native-toolbar
git pull --ff-only
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Build -Apply -WithBranding
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Consolidate-AeroHelmWindows.ps1 -Stage Verify
```

The GitHub Windows job also archives `vihanga-ci-native-evidence.json` with
compiled EXE/DLL hashes. The artifact is compilation evidence only;
rollback tests restore the simulated previous installation afterward.

The assessment tool reads the currently installed EXE/DLL hashes and
matches them to the exact Git commit. It does not execute vehicle
commands or modify flight parameters:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-VihangaCertification.ps1 -RunSourceTests -CheckGitHubCI -Export
```

If cloud integration is available, run the existing M1 private
preflight with physical device observation enabled. Credentials are
requested interactively; the generated report excludes tokens:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmPrivatePreflight.ps1 -CloudUrl $env:AEROHELM_CLOUD_URL -RequireConnectedPi -RequireCompleteParameters
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-VihangaCertification.ps1 -RunSourceTests -CheckGitHubCI -CloudReportPath '.\build-logs\aerohelm-private-preflight.json' -Export -RequireSoftwareGate
```

Do not mix M1 login tokens with R2 OIDC tokens. An R2 endpoint response
is not proof that the M1 operator session works with R2. Make sure the
GitHub workflow `AeroHelm Cloud Plugin Validation` has an actual successful
run for the exact commit SHA; older master builds and status-text source
checks are insufficient.

## Human and hardware acceptance

The field operator and an independent reviewer must attach records for:
Windows GUI and plugin load at standard/high DPI; actual cloud
authentication and expiration; separate cloud versus local USB/radio
connection selection; low-bandwidth and outage/reconnect conditions;
OPS simulator start/abort/lease expiry; INTEL fleet provenance and
parameter completeness; fully disarmed propeller-free Pixhawk/Raspberry Pi
bench test; RC/failsafe behavior under cloud and radio loss; and recorded
autopilot firmware/configuration and serial-port ownership.

Copy `docs/VIHANGA_OPERATOR_ACCEPTANCE.template.json` outside the Git
checkout, fill only after completing the real tests, and attach links to
independently retained logs/evidence. The JSON fields are declarations,
not proof of testing. A reviewer must verify attached logs separately.
The assessment can record those declarations but **always** reports
`operational_flight_certified=false` pending separate, signed
organizational and regulatory acceptance.

No live hardware test is started automatically. Never remove propellers,
power up equipment or conduct a real-world flight without the applicable
site-specific procedures and authorized personnel.

## Unresolved release blockers

- Exact-head CI run must exist and pass the full native Windows build and
  read-only protocol tests.
- M1/R2 authentication and API compatibility must be proven on deployed
  infrastructure, not inferred from source routes.
- Windows UI and native DLL smoke tests require the target workstation.
- Cloud/Pi/hardware, independent direct radio and autopilot failsafe
  acceptance require an authorized physical bench.
- Final release authorization requires a reviewer and documented rollback.
