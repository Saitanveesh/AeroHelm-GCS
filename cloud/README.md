# Cloud fleet coordination research

**Status:** separate fleet-management and simulator-oriented development code. **Not** the live Vihanga Pi-to-Oracle MAVLink relay and not a physical aircraft command service.

The deployed single-aircraft telemetry transport is under [`services/vihanga_link/`](../services/vihanga_link/README.md). The operator-facing system is [Vihanga GCS](../README.md). The `cloud/` application is a standalone Python service with its own database, identity and workflow model. It does not build, modify or launch the native Windows Mission Planner application.

This directory retains earlier AeroHelm R1/R2 terminology in API names, schema identifiers and historical sections for compatibility. Treat its deployment instructions as a **separate experimental system**, not instructions for modifying the working Oracle `:8443` relay.

## Execution boundary

This is a **virtual-only engineering release candidate**. Production
deployment and physical flight are **not authorized** by this source.
The service supports multiple distinct UAV *identities*, physical-UAV
**observation**, durable mission/lease/job records, and simulator-only
workflow dispatch. It does not implement flight controller stabilization,
cloud arming, mode changes, MAVLink commands, actual mission upload,
navigation, airspace approval or safety-case acceptance.

## Services

| Component | Scope |
| --- | --- |
| `app/main.py` | FastAPI REST control plane, tenant-scope, identity, missions, leases, inbox/outbox |
| `app/planner.py` | Deterministic multi-UAV assignment, operator-declared energy reserve |
| `app/models.py` | Durable PostgreSQL fleet, mission, job, ACK and telemetry state |
| `app/security.py` | Signed RS256 OIDC tokens in production; test-only HS256 prohibited in production |
| `app/scheduler.py` | Database expiry sweeper; never automatically requeues an expired job |
| `app/analytics.py` | Cloud-side, tenant-scoped fleet and mission analysis requests and durable results |
| `app/compute_worker.py` | Separate isolated analytics processor; no GCS or FC dependencies |
| `edge/bridge.py` | Pi read-only normalized observation uplink, including optional bounded device-reported ATTITUDE; never opens UART |
| `tools/sim_edge.py` | Workflow-only fake edge ACKs; no flight dynamics or FC access |
| `tools/virtual_world.py` | Bounded multi-UAV virtual route interpolation and sequential telemetry; **not** SITL or actual flight |
| `tools/doctor.py` | Safe non-deploying local unit, disposable PostgreSQL and image verification |
| `tools/telemetry_uplink.py` | HTTPS normalized observation sidecar for existing Pi UART-owner; never replays stale samples |
| `migrations/` | Alembic schema revision with explicit startup version pinning |
| `compose.yaml` | Private reference PostgreSQL/API/scheduler/compute environment, not Internet-facing |
| `tests/` | Safety, tenant isolation, epoch fencing, state transitions, persistence and replay contracts |

## Device-reported attitude (R2 staging; not the live M1 HUD)

The Pi UART-owning agent may supply actual MAVLink ATTITUDE roll/pitch as
`roll_deg`, `pitch_deg` and `attitude_age_ms` together to the bounded
observation sidecar. R2 validates types/ranges, stores the authenticated
device observation and publishes a freshness-gated `attitude` object
from GET `/api/v1/vehicles/{vehicle_id}/telemetry`. The isolated
`fleet_health` worker reports `device_reported_attitude_sources`.
No flight commands or reconfiguration of the FC are performed.

**R2 has not been deployed to the running M1 endpoint.** M1 operator JWT
is not interchangeable with R2 OIDC service identity. This addition
does not repair missing ATTITUDE packets in M1 WebSocket forwarding or
prove a physically valid horizon; the Pi UART owner and M1 relay need
their own real-packet acceptance tests.

## Prerequisites

Python 3.12, PostgreSQL 17, a trusted OIDC identity provider issuing RS256
tokens with pinned issuer/audience/JWKS, a reviewed private TLS gateway,
secret-manager provisioned database URL, explicit `AEROHELM_ALLOWED_HOSTS`
private-host allowlist, encrypted backups, audit retention,
network firewall/mesh ACLs and a trained on-site flight safety authority.

In the repository root, install dependencies into a private Python venv:

```bash
python -m venv .venv-cloud
. .venv-cloud/bin/activate
python -m pip install -r cloud/requirements-dev.txt
python -m cloud.tools.doctor --mode unit
```

For disposable PostgreSQL concurrency tests and a cloud Docker image build,
see [the cloud acceptance runbook](ACCEPTANCE_RUNBOOK.md).
Do not run PostgreSQL schema tests against an operational database.

Tests never talk to an aircraft. They use SQLite-in-memory or a temporary
SQLite file as a **test-only** substitute for PostgreSQL. Real deployment
requires PostgreSQL migration and concurrency tests.

### Production migration and process

```bash
export AEROHELM_CLOUD_MODE=production
export AEROHELM_DATABASE_URL_FILE=/run/secrets/app_database_url
export AEROHELM_OIDC_ISSUER=https://YOUR-TRUSTED-IDP/realms/aerohelm
export AEROHELM_OIDC_JWKS_URL=https://YOUR-TRUSTED-IDP/realms/aerohelm/protocol/openid-connect/certs
export AEROHELM_OIDC_AUDIENCE=aerohelm-cloud
export AEROHELM_ALLOWED_HOSTS=aerohelm-private.example.invalid
export AEROHELM_VIRTUAL_DISPATCH_ENABLED=false

# Schema migration is a distinct privileged operation.
# On Oracle use the one-shot Compose migrate service with the DDL-only credential;
# do not migrate as the restricted API database user.
export COMPOSE_PROJECT_NAME=aerohelm-cloud-r2
docker compose -f cloud/compose.yaml -f cloud/compose.oci.yaml up -d database
docker compose -f cloud/compose.yaml -f cloud/compose.oci.yaml --profile migrate run --rm migrate
docker compose -f cloud/compose.yaml -f cloud/compose.oci.yaml up -d api scheduler compute
```

Do not enable public API ingress by binding 0.0.0.0 without a private,
authenticated TLS reverse proxy. The Docker container listens internally
on port 8080 and the reference Compose file publishes no host ports.
A separately managed gateway is required.

`python -m cloud.app.scheduler` should run as a supervised separate process
to expire claims and leases promptly. Its absence must be monitored. The
scheduler does not forward any commands, so restarting it cannot replay jobs.

## Identity contract

Tokens MUST be signed by the configured OIDC issuer and contain:

- `iss`, `aud`, `iat`, `nbf`, `exp`, `sub`, `tenant`, `kind`, `scope`.
- `kind=operator`: explicitly scoped `fleet:read`, `fleet:admin`,
  `fleet:operate`, `mission:write`, `mission:approve`,
  `mission:execute`, `audit:read`, or `scheduler:tick`.
- `kind=device`: scope `telemetry:write`, `jobs:claim`, `jobs:ack`
  with a signed `vehicle_id` claim. A device token never receives an
  operator role.
- `tenant`: signed tenant ID; any client-supplied tenant header is ignored.

Use separate certificate and workload identities at the edge gateway.
A device token is a logical JWT principal, not cryptographic proof that
the Pixhawk hardware is genuine. mTLS certificate provisioning and actual
onboard identity attestation are independent release gates.

## Core state machine

```text
DRAFT --different approver+SHA256--> APPROVED_SIM
APPROVED_SIM --exclusive virtual vehicle leases--> RUNNING_SIM
RUNNING_SIM --all virtual ACKs--> COMPLETED_SIM
RUNNING_SIM --timeout/rejected ACK--> FAILED_SIM
RUNNING_SIM --lease loss--> LEASE_LOST
DRAFT / APPROVED_SIM / RUNNING_SIM --explicit abort--> ABORTED_SIM
```

Per virtual job:
`PLANNED -> QUEUED_SIM -> CLAIMED_SIM -> ACCEPTED_SIM -> COMPLETED_SIM`;
any claim timeout or rejection becomes terminal and is never automatically
redelivered to an aircraft. Every device claim carries a random, one-time
capability, an expiry and the current vehicle lease epoch. A device ACK
cannot be used as an FC execution acknowledgment.

Analytics and compute are cloud-owned: clients with `analytics:submit` scope POST
`/api/v1/cloud-compute/jobs` with `fleet_health` or `mission_portfolio`,
then read their own report using `GET /api/v1/cloud-compute/jobs/{id}`.
The queue is PostgreSQL-backed; an isolated worker processes at most 16 queued
jobs per tenant, with 512 retained results per tenant. Both request and output
are read-only, strictly bounded, and never authorize flight commands. See
[thin-client offload contract](THIN_CLIENT_OFFLOAD.md).

Telemetry sessions are independent: the server issues a monotonically
increasing epoch per vehicle; client sequence must increase.
The provided `ObservationUplink` creates explicit cloud sessions, uses
fresh device JWTs supplied by the caller, rejects unexpected fields and
follows no HTTP redirects. It reads **already normalized samples only**
from the existing Pi UART-owning software; there is no competing serial
connection, MAVLink command forwarding, or offline telemetry replay. Reconnect
clears the previous session's displayed observation, and the server
marks telemetry stale after five seconds without new accepted samples.

## Primary API endpoints

| HTTP | Path | Scope |
| --- | --- | --- |
| GET | `/api/v1/capabilities` | fleet:read |
| POST/GET | `/api/v1/vehicles` | fleet:admin / fleet:read |
| POST | `/api/v1/vehicles/{id}/lease` | fleet:operate |
| POST | `/api/v1/vehicles/{id}/lease/renew` | fleet:operate |
| POST | `/api/v1/missions` | mission:write |
| GET | `/api/v1/missions` | fleet:read (bounded tenant pagination) |
| POST | `/api/v1/missions/{id}/approve` | mission:approve |
| POST | `/api/v1/missions/{id}/start` | mission:execute |
| POST | `/api/v1/missions/{id}/abort` | mission:execute |
| GET | `/api/v1/missions/{id}` | fleet:read |
| POST | `/api/v1/device/jobs/claim` | device jobs:claim |
| POST | `/api/v1/device/jobs/{job}/ack` | device jobs:ack |
| POST | `/api/v1/cloud-compute/jobs` | analytics:submit (cloud background reporting) |
| GET | `/api/v1/cloud-compute/jobs/{id}` | fleet:read (requesting operator only) |
| POST | `/api/v1/device/telemetry/session` | device telemetry:write |
| POST | `/api/v1/device/telemetry` | device telemetry:write |
| GET | `/api/v1/fleet/status` | fleet:read |
| GET | `/api/v1/fleet/metrics` | fleet:read |
| GET | `/api/v1/events` | audit:read |
| POST | `/api/v1/internal/sweep` | scheduler:tick |

All responses intentionally report `physical_control_authorized=false`
or equivalent. No endpoint accepts raw MAVLink bytes or arbitrary commands.

## Production promotion gates

For the current deployed Vihanga capability and qualification limits, see [project status](../docs/AEROHELM_STATUS.md). Any cloud-fleet production promotion requires a separate approved deployment and safety review.
A passing test suite alone is not a production or flight-safety approval.
