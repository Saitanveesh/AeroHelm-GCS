# Vihanga — new Oracle M1 instance cutover and control authority gates

**Operational update:** Deploy alongside the existing Oracle API using the
[verified live-link runbook](VIHANGA_ORACLE_PI_LIVE_LINK_DEPLOY.md).
The VM can retain the SAME private Tailscale hostname: the legacy AeroHelm
service remains on HTTPS :443, and the new Vihanga relay uses HTTPS :8443
to localhost :8765. Always preserve the explicit :8443 port when checking
the Pi's Oracle-instance binding. The old application cannot be assumed
to implement Vihanga's dedicated routes.

The old M1 service is a separate deployment. Changing Windows source does not
install the new relay or Pi agent. Do not reuse old operator passwords or Pi
device tokens across instances.

## Identify the correct Oracle compute instance

On the **new** Oracle Linux VM, after authenticated SSH access:

```sh
curl -fsS --max-time 4 -H 'Authorization: Bearer Oracle' \
  http://169.254.169.254/opc/v2/instance/ | \
  python3 -c 'import json,sys; a=json.load(sys.stdin); print(a["id"])'
```

That prints the full `ocid1.instance...` (compute instance identity), not a
public IP, tenancy OCID, compartment OCID, or Tailscale hostname.
Keep the full value in your private deployment configuration. The matching
private HTTPS Tailscale hostname must be found independently via the tailnet.

On the new VM, set `VIHANGA_ORACLE_INSTANCE_OCID` in the protected relay
environment together with its existing secrets, `VIHANGA_ALLOWED_HOSTS`,
`VIHANGA_VEHICLE_ID`, `VIHANGA_FC_SYSID` and all required identity controls.
The relay **refuses to start** if IMDSv2 cannot confirm this instance's OCID.
Its `/health` publishes only the SHA-256 identity digest and verification flags,
never the complete OCID.

On the Raspberry Pi, use the **same expected instance OCID**, plus the actual
pinned `VIHANGA_PINNED_RELAY_HOST` and
`VIHANGA_DEVICE_WS_URL=wss://<verified-host>/ws/mavlink/device/<aircraft>`.
The Pi now verifies the bound relay over private HTTPS **before** transmitting
its device bearer token, and rechecks on reconnect. Never run two Pi processes
owning the same FC serial port.

Windows, from the verified `master` checkout:

```powershell
$ocid = 'REPLACE-WITH-FULL-NEW-COMPUTE-INSTANCE-OCID'
$origin = 'https://REPLACE-WITH-NEW-RELAY.ts.net'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-VihangaGCS.ps1 `
    -LinkOrigin $origin -OracleInstanceOcid $ocid -NoAutoTcp
```

The normal startup credential screen for the **legacy M1 backend** is skipped
for this explicitly selected dedicated relay. PATHS prompts for its own
operator login only after verifying that the private origin is bound to the
expected new Oracle compute OCID. An OCID alone is **not** a domain name, TLS
identity, JWT or a substitute for a protected Tailscale network.

A direct USB/telemetry-radio backup remains an independently selected path.
It must not be silently transferred to the private cloud.

## Command/mission milestone — what is actually implemented

`services/vihanga_link/supervised_control.py` provides a simulator-only
authority model for typed parameter writes, mission upload and mission start.
It requires two independent identities, an exclusive expiring vehicle lease,
a matching immutable mission digest, monotonically increasing sequence numbers,
bounded request TTLs, idempotent correlation IDs, and fresh evidence covering
GPS, EKF, battery, geofence, pilot override and failsafes. It distinguishes a
device transport ACK from a flight-controller result ACK.

**It cannot issue physical MAVLink messages, change Pixhawk parameters, arm
or execute a mission.** The currently shipped native Windows bridge is
receive-only and drops outgoing Mission Planner MAVLink. The running legacy M1
backend and UART-owning Pi program are also not replaced by these commits.

Physical USB-equivalent authority requires separately developed and accepted
control-plane transport, a real Pi UART command arbiter, firmware-aware
parameter and mission upload state machines (including FC ACK/readback),
onboard failsafes and verified pilot takeover. These gates must pass hardware
bench tests, SITL/HIL and real link-loss injection before enabling flight.

## Acceptance evidence, not a source-label change

1. Confirm new Oracle instance IMDSv2 OCID matches the configured OCID, with
   new private HTTPS origin and isolated device/operator credentials.
2. Observe real physical FC HEARTBEAT ID0, ATTITUDE ID30 and position ID33/
   VFR_HUD ID74 from Pi, new relay and native Mission Planner DATA/Quick.
3. Observe stale-state/dropout/reconnect handling and independent radio recovery.
4. Validate command lease, two-person approval, sequence/TTL/replay handling,
   physical FC ACK and post-write parameter readback in **SITL/HIL**.
5. Complete propellers-off physical testing with a documented on-site pilot,
   arm/abort protection, RC takeover and explicit independent sign-off.

Do not edit release manifests, fake counters, or mark physical flight
authorized just because source tests pass.
