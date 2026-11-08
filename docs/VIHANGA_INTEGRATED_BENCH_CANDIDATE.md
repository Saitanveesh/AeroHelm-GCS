# Vihanga integrated cloud-control bench candidate — 8 November 2026

**Release designation:** INTEGRATED SOURCE CANDIDATE / PHYSICAL FLIGHT RELEASE NOT ACCEPTED.

All code from parent branches PR #48 telemetry recovery, PR #49 independent pilot/approver authority, PR #50 pilot-to-Pi transport probe, and the typed ARM/DISARM downlink feature is ancestry of this single release candidate branch.

Branch: `release/vihanga-integrated-bench-candidate-20261108`.

## One checkout — preserve the verified master runtime

Run from Windows PowerShell. Do not reset the verified GCS checkout or overwrite the current MissionPlanner.exe, plugin DLL or build manifest.

```powershell
cd C:\Users\saita\AeroHelm-GCS
git fetch origin
git worktree add -b release/vihanga-integrated-bench-candidate-20261108 `
  C:\Users\saita\Vihanga-BenchCandidate `
  origin/release/vihanga-integrated-bench-candidate-20261108
cd C:\Users\saita\Vihanga-BenchCandidate
python -m pip install -r services/vihanga_link/requirements-test.txt
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-VihangaIntegratedAcceptance.ps1 `
  -LinkOrigin 'https://aerohelm-vcn.tailf3ab6a.ts.net:8443'
```

If the candidate branch already exists locally, inspect it instead of force-overwriting local changes. For subsequent source updates use `git pull --ff-only origin release/vihanga-integrated-bench-candidate-20261108` from the candidate worktree.

### Existing verified Windows GCS

The production Windows launcher intentionally rejects unreviewed branch commits/binary hashes. Keep launching the current verified `master` checkout with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 `
  -LinkOrigin 'https://aerohelm-vcn.tailf3ab6a.ts.net:8443' -NoAutoTcp
```

That remains the real receive-only native Mission Planner cloud transport. A candidate Git checkout is NOT an installed GCS binary.

## Cloud routing architecture in the candidate

Windows typed requester -> private Oracle HTTPS :8443 -> separate Argon2id pilot JWT + independent approver approval -> expiring exclusive lease -> typed ARM/DISARM JSON -> existing authenticated single Pi WSS -> Pi sole UART owner -> `PiCommandAdapter` -> real FC COMMAND_ACK -> separate Pi and FC receipts.

The cloud does NOT forward arbitrary Mission Planner MAVLink bytes, invoke FORCE_ARM, allow in-flight force-disarm, or defeat the local RC. `ARM` and landed `DISARM` are the only typed physical actions wired in this slice. Parameter/mission mutations and autonomous execution are still separate unconnected microservices.

## New FC-owned physical evidence

`PhysicalFCObserver` validates exact FC SYSID/COMPID, ArduCopter HEARTBEAT, ATTITUDE, GPS fix, valid HOME_POSITION, SYS_STATUS battery reserve, EKF_STATUS_REPORT, FENCE_STATUS, RC_CHANNELS, EXTENDED_SYS_STATE landed state and HIGH_LATENCY2 failsafe flags. All independent inputs have finite freshness windows. A reboot or heartbeat gap invalidates the old position/health epoch. Missing packets remain UNKNOWN and prohibit authorization.

`LocalGPIOInterlock` requires four distinct, positive-proof, active-high input circuits on a Pi gpiochip: hardware key, local pilot presence, separate RC availability, and RC override clear. A root-owned 0640 configuration `/etc/vihanga/pilot-interlock.json` is read by the `vihanga` process. If the file, gpio chip, binding or any line is missing, all gates close. GPIO must be provided by an independently verified, normally-safe hardwired circuit. No GPIO outputs are set by the program.

**No GPIO chip name, offsets, wiring, power or pin numbers are supplied by GitHub:** those values must be established and continuity-tested on the exact physical Pi 5 hardware. Do not copy a guessed pin map. This module uses libgpiod Python v2, which must be installed and accessible to the service account for physical qualification; missing module denies actuation but does not stop telemetry.

An *illustrative schema, not a usable configuration*:

```json
{
  "chip": "/dev/gpiochipREPLACE",
  "key_line": -1,
  "pilot_line": -1,
  "radio_line": -1,
  "override_clear_line": -1
}
```

This deliberately invalid example cannot enable motors. Store no cloud credentials or operator tokens in the GPIO configuration. The RC takeover circuit and motor inhibit must be independent of Pi power and cloud availability.

### Oracle authorization toggles

- `VIHANGA_CONTROL_SIMULATOR_ONLY=1`: enable isolated pilot/approver credentials and policy engine (not physical flight).
- `VIHANGA_TYPED_COMMAND_DOWNLINK=1`: install typed Oracle downlink protocol (not sufficient for flight).
- `VIHANGA_PHYSICAL_RELEASE_ACCEPTED=1`: independent explicit software acceptance gate after qualified safety review; **never set based on network latency, unit tests or screenshots**.

These are *not* directions to enable flight today. Default is zero for all three. Even when enabled, current FC evidence and all Pi GPIO inputs must pass; the Pi rechecks on the UART owner thread immediately before sending.

## Acceptance evidence to capture

1. Offline Python tests, Windows source-contract scan, PowerShell parser and actual native Windows compile from exact source.
2. Authenticate pilot and approver separately; verify viewer token rejection, expired leases, replay, reconnect and FC reboot invalidation.
3. SITL/HIL motor-inhibit rig: correct command ID 400, param2=0, genuine FC COMMAND_ACK, positive HEARTBEAT armed-state transition, dropped ACK and link failure handling.
4. Propellers removed: verify physically separate radio takeover and hardwired motor inhibit, every interlock line low/high, Pi reboot, FC reboot and Tailscale outage.
5. Verify FC firmware-specific EKF, fence and RC output message availability. `HIGH_LATENCY2` failsafe is often absent on standard streams; missing it intentionally keeps `failsafe_clear=False`. No bypass is provided.
6. Bench validation of disarmed-only mission upload/readback, PARAM_SET/PARAM_VALUE, calibration and onboard autonomous failsafes before adding these to the live UI.
7. Licensed/test-site requirements, on-site pilot in command, field flight tests and signed acceptance before any unsupervised autonomous operation.

### Read-only cloud inspection

`GET /api/v1/flight-control/status` reports configured controls and evidence age without authorizing flight. Pilot token can inspect `GET /api/v1/vehicles/AH-HW-001/control/safety` for reason-coded missing FC fields. `scripts/Invoke-VihangaIntegratedAcceptance.ps1` checks the live relay without sending aircraft commands.

## Known non-completed gates

The native Mission Planner TCP bridge still discards outbound raw MAVLink. Calibration, USB-equivalent parameter requests/writes, full mission upload/start, onboard RC priority evidence beyond the explicitly wired circuit, autonomous failover and a certified flight envelope are NOT signed off. CI from connector commits is not a replacement for a Windows build, SITL/HIL or physical bench.

**Do not merge to master or replace running Oracle/Pi units until tests, physical safety wiring and acceptance evidence are recorded.**
