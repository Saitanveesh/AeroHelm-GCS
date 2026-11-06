# Raspberry Pi observation bridge — cloud Phase 2

This is a **stdlib-only** drop-in observation adapter in the authoritative
`Saitanveesh/AeroHelm-GCS` repository. It reads normalized JSON lines on
stdin from **the existing single Pixhawk/UART-owning Pi agent**. No additional
serial port, MAVLink reader, airborne command sender or public Pi listener is
created.

The legacy Pi agent remains the aircraft-facing telemetry owner. It should
publish a nonblocking, bounded latest-observation feed after parsing genuine
MAVLink packets; an operator must verify that implementation on the physical
Pi. This repo does NOT contain or alter the running Pi UART agent, so that
local producer integration is **NOT completed by these files**.

## Live observation input contract

One UTF-8 JSON object per line, max 2048 bytes:

```json
{"captured_at_ms":1770000000000,"mode":"STABILIZE","armed":false,"latitude":12.9716,"longitude":77.5946,"altitude_m":31.2,"battery_pct":72.0,"gps_fix_type":3}
```

`captured_at_ms` must be UNIX milliseconds recorded immediately when the
existing Pi agent observes the live packet. The example timestamp is
**illustrative**; use the actual current clock during operation. Samples
older than 2.5 s (or more than 0.5 s in future) are dropped. Reject unknown
fields, NaN, invalid coordinates and invalid types. The Pi bridge
maintains a 16-record drop-oldest queue and never blocks the UART reader
during cloud backpressure. No historical log replay is treated as live.

If a POST result is ambiguous or session is lost, the bridge **discards
the sample**, opens a new fenced cloud telemetry epoch and only accepts
a new fresh observation. A server 401 invalidates the cached IdP access
token; the caller never logs access/refresh credentials.

## Identity and network

The Pi has a distinct, least-privilege OIDC confidential service-client
identity. Provision `tenant`, `kind=device`, **exact registered
`vehicle_id`**, and scope **`telemetry:write` only** in signed RS256
JWTs. Do NOT issue `fleet:operate`, `mission:execute`, `jobs:claim`
or `jobs:ack` to physical Pi observation clients.

The bridge obtains short-lived OIDC tokens using HTTPS client credentials
and a locally restricted `0600` secret file supplied by the host's secret
manager. Mutual TLS and OAuth certificate-bound tokens (RFC 8705) are
**future independent requirements**; bearer JWT over private HTTPS is not
hardware attestation. Transport validates system CA or an explicitly
configured trusted CA bundle. HTTP redirects are disabled.

On the Pi, after reviewing the script and connecting the producer's
live normalized stdout pipe:

```bash
python3 -m cloud.edge.bridge \
  --origin 'https://YOUR-ORACLE-TAILNET-NODE.ts.net' \
  --token-endpoint 'https://YOUR-TRUSTED-IDP/oauth2/token' \
  --client-id 'aerohelm-uav-unique-identity' \
  --secret-file '/run/credentials/aerohelm-pi-bridge/client_secret'
```

Do not use the illustrative URLs as real configuration. The operator must
supply a **live line source to stdin** from the already-running Pi telemetry
agent. Do not open a second serial reader, disable FC failsafes, or wire the
bridge to Mission Planner commands.

## Independent verification

```bash
python -m pytest -q cloud/tests/test_pi_edge_bridge.py
```

Tests use fake HTTP/OIDC endpoints and fake telemetry inputs; they never
contact a real drone. Check that telemetry goes STALE on a link outage,
old session epochs are rejected and direct local pilot control remains
operational without cloud connectivity.

This sidecar does not make the platform flight-ready. Real pairing,
OAuth client provisioning, and Pi maintenance window are pending.
