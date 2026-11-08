# Vihanga typed cloud ARM/DISARM transport

**Source stage (8 November 2026): typed bidirectional command routing implemented but PHYSICAL ARM/DISARM NOT QUALIFIED OR ENABLED.**

A successful 286 ms Oracle/Pi transport ping proves one authenticated private network round trip. It does not establish physical RC override, FC safety flags, COMMAND_ACK correlation, motor safety, reliable packet delays, or actual Windows GCS command parity.

## Verified endpoint configuration from repository

- Windows relay origin: `https://aerohelm-vcn.tailf3ab6a.ts.net:8443`
- Local private Oracle relay: `http://127.0.0.1:8765`
- Pi private publisher: `wss://aerohelm-vcn.tailf3ab6a.ts.net:8443/ws/mavlink/device/AH-HW-001`
- Windows native Mission Planner bridge: `127.0.0.1:5760` (STILL read-only)

From the existing verified Windows `master` release directory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 `
  -LinkOrigin "https://aerohelm-vcn.tailf3ab6a.ts.net:8443" `
  -NoAutoTcp
Invoke-RestMethod -Uri "https://aerohelm-vcn.tailf3ab6a.ts.net:8443/health" -TimeoutSec 10
```

Do not run the Vihanga startup launcher from this non-master branch: the launcher checks the release manifest, exact source commit and compiled binaries. Keep the existing signed local release and backup independent RC/USB path.

## Source added in this branch

- `typed_command_downlink.py`: ARM/DISARM-only typed envelope, exact expiry, request ID/epoch binding, Pi queue, replay protection, separate local-safety verification, and UART-thread-only `PiCommandAdapter`.
- `control_api.py`: pilot-only `POST /api/v1/vehicles/{vehicle}/control/commands` issues a typed WSS downlink only after the independent safety approver consents to the exact safety-plan SHA-256, the exclusive pilot lease is current and verified fresh vehicle evidence passes.
- `relay.py`: sends through only the currently authenticated Pi device socket and consumes strict Pi and FC receipt types from that exact connection, maintaining separate transport vs. FC ACK states.
- `pi_agent.py`: receives bounded JSON command envelopes on the authenticated WSS; queues to the existing sole UART owner thread; sends back distinct local denial, interlock-confirmed UART transmission, and physical FC COMMAND_ACK receipts.
- `scripts/Request-VihangaTypedArmDisarm.ps1`: interactive Windows two-person operator flow with a real SHA-256 safety-plan file, ARM/DISARM confirmation, scoped tokens, command receipt polling and lease release.

The actual MAVLink encoder in `pi_control_adapter.py` uses `MAV_CMD_COMPONENT_ARM_DISARM = 400`; for ARM param1=1, DISARM param1=0, param2=0 (never force ARM/DISARM). It is invoked only on Pi's sole UART owner thread after Pi-local safety provider verification.

## Critical fail-closed integration boundary

The Oracle `ControlContext._physical_evidence` provider intentionally reports unknown RC, EKF, geofence, battery, disarmed/on-ground and failsafe evidence. The Pi `PiTypedCommandEngine` intentionally defaults its independent hardware gate to CLOSED and its real FC safety evidence provider to NONE.

Therefore, **setting `VIHANGA_TYPED_COMMAND_DOWNLINK=1` does NOT enable aircraft actuation.** A command must still pass the Oracle broker and then the independent Pi-local gate. There is deliberately no `ARM_ANYWAY`, remote safety assertion, raw COMMAND_LONG or unrestricted WSS binary injection.

On the current source branch, without a certified local key/RC/FC evidence collector, an ARM/DISARM attempt is rejected by the Oracle relay with an HTTP 409. This is expected, not proof of a broken network.

## Safe source staging without replacing working telemetry

On each maintenance system, preserve the existing live tree and independent services. Inspect only the branch in a separate checkout rather than resetting master or overwriting signed binaries:

```bash
git clone --filter=blob:none --single-branch --branch feat/typed-command-downlink-v1-20261108 \
  https://github.com/Saitanveesh/Vihanga-GCS.git vihanga-control-stage
cd vihanga-control-stage
git rev-parse HEAD
```

Do not install or restart the live Pi/Oracle services with this branch merely to test arming. Deployment requires platform-specific service-file, dependency, NTP, physical GPIO/RC and FC evidence review, reboot/rollback behavior and supervised propellers-removed safety acceptance. The working receive-only telemetry system can continue operating from master.

## Windows interactive control request after the gates are complete

Only when the relay and Pi have independently documented and accepted the genuine local safety evidence, run the independent two-person requester from this branch:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Request-VihangaTypedArmDisarm.ps1 `
  -Action ARM -PlanFile ".\bench-safety-plan.txt" `
  -Origin "https://aerohelm-vcn.tailf3ab6a.ts.net:8443"
```

The two operator roles must be independently provisioned; do not reuse telemetry viewer passwords. A positive physical `COMMAND_ACK` only means that the FC accepted a request, NOT that the motors actually armed. Follow with independently verified real FC HEARTBEAT armed-state transitions; keep propellers removed and the physical pilot/RC takeover at hand. Never assume that a cloud timeout means a command did not execute, and NEVER retry automatically.

## Minimum work to qualify real flight authority

1. Implement on-Pi hardware switch/key evidence and RC availability from independently verified physical sources, with no remote-controlled bypass.
2. Decode exact FC-owned heartbeat, EKF, fence, battery, GPS, ground state and failsafes from the selected real ArduCopter firmware with freshness and identity checks on both sides.
3. Confirm RC priority and cloud-link loss behavior on an isolated SITL/HIL rig and a propellers-removed supervised bench, including serial reboot and duplicate ACK cases.
4. Link an independently approved native Windows flight-control panel to the typed HTTP API. The old 5760 MAVLink bridge **must remain receive-only**; raw USB-equivalent native commands are not forwarded.
5. Qualify command completion separately from COMMAND_ACK; audit/replay handling and durable independent override must be retained.

**The source code is not operational flight-control certification.** Do not deploy unverified actuation into a connected airframe.
