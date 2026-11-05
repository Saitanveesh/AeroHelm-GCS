# AeroHelm — dual link, real-only cloud attitude and Kerberos release plan

**Source lock:** `feat/gcs-research-cybershield-twin-ui` (reviewed development,
not automatically production-deployed). Current GCS build must be repeated
after source changes; exact EXE and DLL SHA-256 are checked by the release
manifest. This is not flight certification.

## New operator model

AeroHelm now has a separate top-toolbar **PATHS** connection module.
TOUCH → FLIGHT uses that same explicit selection.

| Path | Native GCS action | Source and authority |
|---|---|---|
| CLOUD VIEW | Retry authenticated loopback TCP 127.0.0.1:5760, then request native Mission Planner Flight Data | Pi sole UART → Oracle binary WSS → read-only bridge. Outbound GCS bytes discarded; requires real HB and ATTITUDE |
| CLOUD ATTITUDE | Separate visual attitude indicator, polled from JWT-protected API | Only genuine, recent decoded MAVLink ATTITUDE 30. Blank/stale on missing data; **not** a certified instrument |
| PILOT DIRECT BACKUP | Show Mission Planner's normal Flight Data COM/baud controls | Independent physical USB / telemetry-radio link selected and connected *deliberately* by the local pilot. May carry full normal native GCS command authority |

No automatic link switching, no auto-command authority transfers, no
artificial HEARTBEAT or ATTITUDE injection, no autonomous flight command
execution. The Pi retains exclusive `/dev/ttyAMA0` ownership. A separate
backup radio must attach through a separately configured FC telemetry port
(typically TELEM2) or direct FC USB; do **not** connect a second reader to
the Pi's UART or assume the radio works just because its Windows COM port
appears.

**Independent RC control and onboard autopilot failsafes remain outside
AeroHelm's cloud data plane.** A secondary telemetry radio is not itself
a substitute for verified RC or GCS-loss failsafe configuration.

## Important distinction — real native HUD versus cloud preview

The previous screenshot showed fresh generic Oracle frames, zero cloud
viewers and native `No Heartbeat Packets Received`. Cumulative frame
counts do not prove a real MAVLink 0 HEARTBEAT or 30 ATTITUDE.

The native bridge is now retryable after startup port/login failure.
The PATHS chooser queries JWT-protected `/api/v1/mavlink/paths`; the
cloud is labeled as ready for HUD data only if actual recent HEARTBEAT
and ATTITUDE have been received. Direct USB/radio is explicitly
`NOT_OBSERVABLE_FROM_CLOUD`.

The separate observational attitude panel polls
`GET /api/v1/mavlink/attitude` and draws a horizon only from finite,
fresh roll/pitch; it hides everything on error, expiry or staleness.
**Neither change can repair a missing FC UART stream.** If real
HEARTBEAT/ATTITUDE counters remain zero, FC serial routing must be
investigated by a qualified operator on a propeller-free bench.

## Windows latest release

In Windows PowerShell, close Mission Planner first:

```powershell
cd C:\Users\saita\AeroHelm-GCS
git status --short
git pull --ff-only origin feat/gcs-research-cybershield-twin-ui
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-AeroHelmWindowsPages.ps1
python -m unittest -v Tools.test_aerohelm_offline_gcs Tools.test_aerohelm_build_globs
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Update-AeroHelmLatest.ps1 -Apply -Launch
```

The updater preserves a complete previous runtime copy, compiles
Mission Planner and integrated plugin, verifies source/release hashes
and starts the current version. It refuses unknown TCP 5760 owners and
does not kill processes by default. Stop only an independently
verified legacy Windows Python bridge in its own terminal, or explicitly
use the existing `-StopVerifiedLegacyBridge` flag.

For **offline local pilot backup** (only an authorized physically present
pilot, with an independent FC USB or telemetry-radio link):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-AeroHelm-DirectBackup.ps1 -AcknowledgePilotAuthority
```

This uses the already verified binary, native `--local-maintenance`
profile, no cloud login, no native cloud bridge and no automatic TCP.
The pilot must select the correct physical COM port and baud and manually
press CONNECT. This path can use standard Mission Planner command
capabilities over a **direct** FC link; it is never called by a cloud
reconnect event or automatic failover.

## Cloud rollout — independent

Current Oracle M1 is old until the specifically reviewed backend image is
deployed. Source has JWT-protected real roll/pitch and link topology.
The optional enterprise SSO exchange and security capability endpoint
are disabled by default / fail closed if not provisioned.

On the Oracle VM, **read-only inspection first**:

```bash
cd ~/AeroHelm
git status --short
git pull --ff-only origin feat/cloud-connectivity-foundation
bash scripts/Stage-AeroHelmOracleM1.sh --inspect
```

Only after confirming the actual Compose/image deployment, having a
verified rollback snapshot, and with the FC disarmed and all propellers
removed in a supervised maintenance window, explicitly apply:

```bash
AEROHELM_BENCH_MAINTENANCE_APPROVED=YES \
  bash scripts/Stage-AeroHelmOracleM1.sh --apply
```

This briefly interrupts the live WSS cloud viewer. It changes **only**
the Oracle API container; it does not deploy V3, install a KDC, program
the FC or restart the Pi agent.

## Kerberos end-to-end integration

The new Windows login panel contains an optional **Windows SSO (Kerberos)**
choice, hidden unless `AEROHELM_KERBEROS_UI_ENABLED=1`. It uses
`CredentialCache` restricted to `Negotiate` and requires the EXACT
administrator-approved `AEROHELM_KERBEROS_SSO_ORIGIN` (not a wildcard
tailnet hostname). Do not enable until a KDC, SPNEGO gateway/keytab,
separate HMAC secret and principal-role mappings are actually tested.

The full provisioning and revocation plan is in AeroHelm backend
`docs/KERBEROS_SSO_DEPLOYMENT.md`. A correct deployment involves
KDC/realm identity, protected HTTP service principal/keytab, private
Apache GSSAPI authentication, localhost-only signed one-time assertions,
JWT exchange, replay detection and controlled rotation. The backend
never trusts unauthenticated `X-Remote-User`.

Kerberos authenticates the **operator once**; it is not an extra
handshake for every MAVLink frame, so normal telemetry latency is
not increased per packet. Do not make Kerberos a hard requirement for
the on-site offline pilot backup path.

## Remaining milestones / architecture roadmap

1. **Physically verify real FC streams** on a disarmed, prop-off bench:
   native HEARTBEAT, ATTITUDE, yaw, battery/GPS and loss/recovery. Compare
   actual tilt against both cloud monitor and native Mission Planner HUD.
2. **Independently wire TELEM2 backup** and check its COM port/baud,
   ArduPilot protocol settings, RC/GCS-loss failsafes and deliberate path
   switching. Never switch control links automatically while airborne.
3. **Transport quality:** per-path time series, jitter/gap distribution,
   explicit WSS viewer queue pressure and expiring rate counters; separate
   observation confidence from safety/airworthiness.
4. **Device authentication:** per-aircraft scoped mTLS credentials,
   rotation/revocation, strict Oracle-to-Pi command deny, per-vehicle
   bounded queues and auditable policy enforcement.
5. **Fleet-aware data plane:** isolated aircraft identities and telemetry
   namespaces, regional relay fanout, stateless read-only CDN/analytics
   with no raw camera uploads and no stale-frame replay as live data.
6. **Real airframe twin:** measure mass/inertia, thrust/motor dynamics,
   wind/sensor timing; run held-out test logs and uncertainty envelopes.
   The current V3 six-DOF lab is a physics sandbox, not a calibrated twin.
7. **Security acceptance:** real Kerberos KDC/SSO test, restricted keytab,
   tailnet ACL evaluation, replay/clock drift probes, backup and restore,
   alerting and WORM-style off-host audit sink.

**None of the unbuilt roadmap work is claimed operational.** Successful
source tests or EXE compilation are not substitutes for physical and
security infrastructure acceptance.
