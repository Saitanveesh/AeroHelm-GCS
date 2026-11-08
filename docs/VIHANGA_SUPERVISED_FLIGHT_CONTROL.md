# Vihanga supervised cloud flight control

**Status: SOURCE DEVELOPMENT / PHYSICAL FLIGHT CONTROL DISABLED.**

The live Pixhawk → Raspberry Pi 5 → Oracle VM → Windows path is currently a
private, authenticated telemetry link. This release adds an authorization
model, simulator-scoped control API and a disconnected Pi command encoder.
**It does not turn the installed Mission Planner connection into a flight
command link and must not be used for unattended flight.**

The project was renamed to `Saitanveesh/Vihanga-GCS`; existing local
checkout directories and compatibility filenames may still say AeroHelm.

## Capability and implementation boundaries

| Requirement | Implementation | Remaining gap |
| --- | --- | --- |
| Signed-in telemetry viewers | Existing Pi publisher, Oracle relay and native 5760 bridge | Already observed on the physical bench; session and GPS verification still required |
| Separate pilot authorization | `control_api.py` uses independent Argon2id pilot credentials and scope-specific short JWTs | Operational identity lifecycle, hardware acceptance |
| Independent approver | Separate approver account and token required per exclusive lease | Second-factor device enrollment and audit retention |
| Exclusive command sequence | `control_policy.py` binds lease, FC identity, telemetry epoch, plan digest, monotonic request sequence and deadline | Persist receipts and review restart behavior |
| RC override / onboard safety | Policy rejects unsafe/stale/failsafe and active override states | **Physical source of RC, EKF, fence, battery and local interlock evidence is not yet connected** |
| Physical command encoding | `pi_control_adapter.py` packs tightly restricted ArduCopter COMMAND_LONG actions using existing sole-owner UART, *if passed an externally verified interlock* | **Not connected to `pi_agent.py` or its WSS receive path; interlock defaults closed** |
| Mission Planner native command protocol | Native 127.0.0.1:5760 bridge still drains outgoing GCS bytes | Bidirectional authenticated bridge with full MAVLink transaction handling |
| Parameter mutation and calibration | Not implemented in physical command bridge | Dedicated PARAM_SET/PARAM_VALUE, COMMAND_LONG/ACK calibration microservices, FC-dependent constraints |
| Mission upload and execution | Offline previews, simulated control and command-intent validation | MISSION_COUNT / MISSION_REQUEST_INT / MISSION_ITEM_INT / MISSION_ACK verified transaction, integrity checks and disarmed staging |
| Live autonomous control | Not implemented | Flight envelope, FC ACKs, independent RC, local killswitch and qualified field testing |

## Architecture target

```text
WINDOWS GCS
  native Mission Planner flight UI + independent pilot consent
       |
       | private authenticated command session
       v
ORACLE VM (private :8443, no public UART)
  flight-operator JWT (NOT telemetry:read)
  separate second-person approval token
  exclusive short lease + approved mission revision
  monotonic command sequence + idempotency + immutable receipt reference
       |
       | typed intents only, never arbitrary caller-supplied MAVLink bytes
       v
PI SOLE-OWNER UART AGENT
  independent local hardware key / watchdog / RC availability
  FC SYSID/COMPID and signed freshness checks
  bounded action-specific MAVLink state machines
       |
       | one command, one tracked FC response
       v
PIXHAWK / ARDUPILOT
  onboard arming checks + geofence + battery + RC/GCS failsafe
       |
       v
FC COMMAND_ACK / PARAM_VALUE / MISSION_ACK
       |
       v
Pi -> Oracle audited receipt -> Windows visible outcome

RC receiver -> Pixhawk: independent local pilot override and recovery
```

**No bridge may forward arbitrary GCS bytes to the aircraft.** Even an
authenticated pilot gets typed commands validated by both the Oracle authority
and Pi-local firmware-specific allowlist. The Pi must be the only writer/reader
of its FC UART. A second MAVLink session must not steal the serial device.

## Current source

- `services/vihanga_link/control_policy.py`: exclusive two-person leases,
  request sequencing, 500–5000 ms deadlines, command allowlist, no replay,
  transport receipts distinct from matching FC ACK results, hash-linked
  in-memory audit entries.
- `services/vihanga_link/control_api.py`: default-disabled private
  `/api/v1/flight-control/` simulator-role API. The physical evidence
  provider explicitly fails closed on missing RC, EKF, battery and fences.
  This API **never sends** to the Pi.
- `services/vihanga_link/pi_control_adapter.py`: disconnected physical
  encoder for `ARM`, landed `DISARM`, `TAKEOFF`, `LAND`, `RTL`,
  verified `SET_MODE` and `MISSION_START`. It has **no default enabled
  interlock** and is not instantiated in the active Pi publisher.
- `services/vihanga_link/mission_transaction.py`: isolated physical MAVLink
  `MISSION_COUNT` → real `MISSION_REQUEST_INT` → `MISSION_ITEM_INT`
  → matching `MISSION_ACK` transaction. It refuses to report a verified
  mission until the FC is downloaded again and matches the approved SHA-256
  route digest. Abort never silently clears the previous mission.
- `services/vihanga_link/parameter_transaction.py`: isolated
  `PARAM_SET` / `PARAM_VALUE` readback transaction restricted to
  approved, bounded `REAL32` parameters while landed and disarmed.
  No blind retries or persistent bypass settings.
- `services/vihanga_link/deploy/configure_control_roles.py`: separate
  simulator-only Argon2id pilot and approver credentials.
- `plugins/VihangaLinkSession.cs`: reads the private control status, no
  control token persistence or aircraft commands.
- `plugins/AeroHelmPlatformV2.cs`: read-only control-readiness display.
- `services/vihanga_link/test_control_policy.py`,
  `test_control_api.py`, `test_pi_control_adapter.py`,
  `test_mission_transaction.py`, `test_parameter_transaction.py`: mocked
  rejection/authorization/ACK tests, **not** Pixhawk acceptance.

## Explicit current default

```text
VIHANGA_CONTROL_SIMULATOR_ONLY=0
Physical command dispatch: DISABLED
Pi local physical interlock: NOT CONNECTED / CLOSED
Native Mission Planner cloud command bridge: RECEIVE-ONLY
Pilot local USB / telemetry backup: INDEPENDENT
```

To provision **only isolated simulator role credentials** on a maintenance
Oracle VM (no physical access and no flight):

```bash
cd /opt/vihanga/source
sudo /opt/vihanga/venv/bin/python \
  -m services.vihanga_link.deploy.configure_control_roles
```

This creates `/etc/vihanga/control.env` mode `0600`, refuses overwrite,
and prompts separately for pilot/approver passwords. The updated systemd unit
optionally reads that separate file. Installing source or provisioning the
file **does not grant physical command authority**.

No production telemetry service needs to be restarted just to inspect source.
Do not add undocumented variables or bypass startup validation.

## Onsite qualification gates before any aircraft actuation

1. **Offline tests:** Python policy and simulator API tests, Windows source
   contracts, exact checkout native Windows solution/plugin build and manifest.
2. **ArduPilot SITL:** complete mission upload and command ACK lifecycle,
   dropped frame/retry, double-click/replay, relay restart, stale epoch,
   new operator login, rejected commands, expired lease and airplane mode
   failover tests.
3. **HIL:** Pixhawk message serialization, correct flight-controller
   system/component identity, RC pilot priority, real firmware mode map,
   geofence/EKF/GPS/battery/armed-state verification and recording.
4. **Propellers removed:** local physical arming switch, forced cloud outage,
   frame starvation, manual RC control, failsafe state transitions and
   parameter/mission transaction integrity with a current FC.
5. **Supervised outdoor acceptance:** qualified local pilot-in-command,
   independent radio, permitted test area, explicit recovery mode and log
   review. No autonomous takeoff approval based on a bench test.
6. **Operational deployment:** secrets rotation, audit persistence and
   tamper evidence, watchdog / local interlock hardening, fleet device
   isolation and documented rollback.

## FC acknowledgment is not task completion

MAVLink `COMMAND_ACK(MAV_RESULT_ACCEPTED)` confirms command acceptance,
**not completion of takeoff or arrival at RTL home**. Mission upload must
receive matching `MISSION_ACK`; parameter change must read back the
expected `PARAM_VALUE`. Any transport-only Pi response must be labelled
as such. Do not resend ARM/TAKEOFF/MISSION_START automatically when an ACK
is lost; require a new observed physical state and explicit authority.

Relevant protocol references:

- [MAVLink command protocol](https://mavlink.io/en/services/command.html)
- [MAVLink mission protocol](https://mavlink.io/en/services/mission.html)
- [ArduPilot arming/disarming](https://docs.ardupilot.org/dev/docs/mavlink-arming-and-disarming.html)
- [ArduPilot copter GCS failsafe](https://ardupilot.org/copter/docs/gcs-failsafe.html)

## Current build and test procedure

```bash
python -m pip install -r services/vihanga_link/requirements-test.txt
python -m compileall -q services/vihanga_link
python -m pytest -q services/vihanga_link/test_control_policy.py \
  services/vihanga_link/test_control_api.py \
  services/vihanga_link/test_pi_control_adapter.py \
  services/vihanga_link/test_mission_transaction.py \
  services/vihanga_link/test_parameter_transaction.py \
  services/vihanga_link/test_link.py
```

On Windows, use the existing transactional `scripts/Repair-AeroHelmWindows.ps1
-Apply` process. Do not replace the verified EXE or plugin manually, edit
the release manifest or overwrite the currently functioning Pi/Oracle
services just to try this branch.

**Rollout blocker:** The live Pi safety adapter, command tunnel, RC and
failsafe evidence collection, mission/parameter state machines, and
independent onsite test evidence remain unfinished. The authorization API
must not be promoted to physical flight control until these are complete.
