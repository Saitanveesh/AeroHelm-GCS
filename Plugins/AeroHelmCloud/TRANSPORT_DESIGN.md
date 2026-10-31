# AeroHelm Desktop → Cloud → Aircraft: transport design notes

## Verified state today

- Mission Planner includes a built-in plugin loader and native USB/TCP/UDP MAVLink interfaces.
- AeroHelm Cloud presently offers authenticated **JSON telemetry ingestion**, WebSocket telemetry broadcast, an operator registry and a read-only *view session* resource.
- The initial AeroHelmCloud Mission Planner plugin uses only the operator-facing registry and view-session API. It does not alter the active \`MainV2.comPort\` transport.

**Do not call a view session a flight-controller connection.** It does not support MAVLink command acknowledgments, parameter download, calibration or mission transfer.

## Future GCS transport adapter requirements

Build a separate, testable transport assembly and a narrowly scoped plugin adapter. Do not modify ArduPilot or remove the original Mission Planner connection options.

1. **Pi uplink**: own the Pixhawk serial port exactly once (or use a deliberately configured MAVLink router). Decode complete MAVLink 2 frames and record their system/component IDs. Never forward arbitrary unframed bytes or mix multiple vehicle IDs.
2. **Cloud broker**: device-authenticated outbound TLS session; operator-authenticated subscription; per-vehicle addressing; bounded buffers and backpressure.
3. **Windows GCS transport**: map vehicle ID to a single authenticated cloud stream; use Mission Planner's existing MAVLink connection abstraction or local loopback adapter, not UI polling for flight data.
4. **Independent onboard safety**: RC/pilot priority, hardware pre-arm checks, a time-bounded authority lease and a documented action for cloud loss must all exist on Pi/FC side, independently of the desktop.
5. **Command handling**: separate an explicitly allowlisted command channel from telemetry. Include authorization state, sequence and monotonic age, vehicle ID, source identity, and a uniquely traceable request ID. No replay on reconnection. Forward a command only when locally authorized; reconcile the real \`COMMAND_ACK\` with the initiating request.
6. **Calibrations**: some procedures require physical motion, disarmed state, local human supervision and FC prompts. Never advertise remote calibration until these are validated case by case.
7. **Video**: WebRTC media relay is separate from MAVLink command traffic. Independent bandwidth and latency monitoring are required.

## Acceptance sequence

- A. Build the unmodified Mission Planner baseline.
- B. Load AeroHelmCloud plugin, authenticate to the private HTTPS server and list a real Pi-reported aircraft.
- C. Verify that Connect creates only an operator view session and that native Mission Planner hardware COM connection is untouched.
- D. Implement **receive-only** MAVLink relay and show actual Pixhawk heartbeat/attitude via a Mission Planner transport.
- E. Add test automation: malformed frames, duplicate/out-of-order messages, multi-vehicle isolation, connection churn, operator expiry and network partitions.
- F. Bench-only command/ACK tests with propellers removed and physical pilot authority; independent safety review before any flight action is enabled.

## Known architecture constraints

AeroHelm Cloud currently stores telemetry and sessions in memory, with a single configured device identity. It is unsuitable for multi-aircraft authoritative command routing. Tailscale/HTTPS secures transport; it does not establish flight safety. The current Mission Planner fork is GPL-3.0 licensed and derivatives require preserving license and source obligations.

No bidirectional command relay is implemented in this branch.
