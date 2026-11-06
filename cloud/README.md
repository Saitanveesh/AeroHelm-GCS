# AeroHelm Cloud Fleet Control Plane R1

**Authoritative repository:** [Saitanveesh/AeroHelm-GCS](https://github.com/Saitanveesh/AeroHelm-GCS).
This is a standalone Python service under `cloud/`. It does not build,
modify, launch, or depend on the native Windows Mission Planner GCS.

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
| `tools/sim_edge.py` | Workflow-only fake edge ACKs; no flight dynamics or FC access |
| `migrations/` | Alembic schema revision with explicit startup version pinning |
| `compose.yaml` | Private reference PostgreSQL/API/scheduler environment, not Internet-facing |
| `tests/` | Safety, tenant isolation, epoch fencing, state transitions, persistence and replay contracts |

## Prerequisites

Python 3.12, PostgreSQL 17, a trusted OIDC identity provider issuing RS256
tokens with pinned issuer/audience/JWKS, a reviewed private TLS gateway,
secret-manager provisioned database URL, encrypted backups, audit retention,
network firewall/mesh ACLs and a trained on-site flight safety authority.

In the repository root, install dependencies into a private Python venv:

```bash
python -m venv .venv-cloud
. .venv-cloud/bin/activate
python -m pip install -r cloud/requirements.txt
python -m pytest -q cloud/tests
```

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
export AEROHELM_VIRTUAL_DISPATCH_ENABLED=false

python -m alembic -c cloud/alembic.ini upgrade head
python -m uvicorn cloud.app.main:create_app --factory --host 127.0.0.1 --port 8080 --workers 1
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

Telemetry sessions are independent: the server issues a monotonically
increasing epoch per vehicle; client sequence must increase. Reconnect
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
| POST | `/api/v1/missions/{id}/approve` | mission:approve |
| POST | `/api/v1/missions/{id}/start` | mission:execute |
| POST | `/api/v1/missions/{id}/abort` | mission:execute |
| GET | `/api/v1/missions/{id}` | fleet:read |
| POST | `/api/v1/device/jobs/claim` | device jobs:claim |
| POST | `/api/v1/device/jobs/{job}/ack` | device jobs:ack |
| POST | `/api/v1/device/telemetry/session` | device telemetry:write |
| POST | `/api/v1/device/telemetry` | device telemetry:write |
| GET | `/api/v1/fleet/status` | fleet:read |
| GET | `/api/v1/fleet/metrics` | fleet:read |
| GET | `/api/v1/events` | audit:read |
| POST | `/api/v1/internal/sweep` | scheduler:tick |

All responses intentionally report `physical_control_authorized=false`
or equivalent. No endpoint accepts raw MAVLink bytes or arbitrary commands.

## Production promotion gates

See [cloud production gates](../docs/CLOUD_CONTROL_PLANE_RELEASE_GATES.md).
A passing test suite alone is not a production or flight-safety approval.
