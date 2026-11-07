# AeroHelm Cloud R1 — Same-day engineering acceptance runbook

**Authoritative repository:** `Saitanveesh/AeroHelm-GCS`
**Cloud-only branch:** `feat/cloud-fleet-control-plane-r1`
**Review:** PR #24, draft until all gates pass.

This runbook validates cloud orchestration **without starting the Windows GCS,
writing FC parameters, contacting an aircraft, or deploying to Oracle**.
The physical command plane does not exist in this R1 implementation.

## 1. Source + unit acceptance on a developer computer

On a clean checkout, preserve local uncommitted work:

```bash
git status --short
git fetch origin
git switch feat/cloud-fleet-control-plane-r1
python3.12 -m venv .venv-cloud
. .venv-cloud/bin/activate
python -m pip install -r cloud/requirements-dev.txt
python -m cloud.tools.doctor --mode unit
```

The doctor compiles all Python modules and runs cloud API, virtual fleet,
telemetry, auth, replay, lease, expiry and virtual route/network-failure tests.
It deliberately removes external DB configuration for unit mode.
**Record command exit status and full test result; never declare PASS just
because source code or a GitHub workflow exists.**

## 2. PostgreSQL 17 acceptance (disposable DB ONLY)

Provision a disposable DB whose name ends `_ci` or `_test`.
Use a throwaway account, isolated bridge network and NO aircraft secrets.

```bash
export AEROHELM_CLOUD_MODE=test
export AEROHELM_DATABASE_URL='postgresql+psycopg://CI_USER:CI_PASS@localhost:5432/aerohelm_ci'
python -m cloud.tools.doctor --mode postgres
unset AEROHELM_DATABASE_URL
```

This intentionally runs Alembic migrations **only against test databases**,
then tests concurrent lease and mission start across two independent API
instances. Reject any production DATABASE_URL or URL_FILE while running
tests. Verify backup/restore separately before real deployment.

## 3. Container and private networking acceptance

```bash
python -m cloud.tools.doctor --mode image
```

The image build installs pinned cloud dependencies and copies only `cloud/`,
not Mission Planner assemblies or artifacts. The example
`cloud/compose.yaml` has separate networks:
- PostgreSQL and scheduler: Docker internal-only network
- API: internal database network PLUS dedicated gateway/IdP-egress network
- **No** host-published API or database ports

To start a real private deployment, an infrastructure engineer must first
provision an OIDC issuer, certs, TLS gateway, a restricted **non-superuser**
application database role, separate schema migration role, secret files,
host allowlist, backups and recovery access. Set dispatch FALSE initially.
Never commit `cloud/.env` or files under `cloud/secrets/`.

## 4. Operator workflow under test-issued credentials

Authenticate as distinct operator principals: fleet administrator, mission
author, mission approver, and mission runner. Register two `SIM-` vehicles,
submit a bounded multi-task mission, verify an immutable SHA-256 manifest,
approve with the separate principal, acquire independent vehicle lease
epochs, and start virtual-only work. Two virtual edge workers claim their
assigned task and ACK `ACCEPTED_SIM` followed by `COMPLETED_SIM`. Confirm
fresh per-aircraft telemetry epochs, ordered sequences, read-only fleet
status and a durable event outbox record.

Required denials: device principal cannot assume operator authority,
creator cannot approve its own mission, hardware vehicle cannot acquire
a flight-control lease, expired job capability cannot ACK, two missions
cannot run on one virtual aircraft, cross-tenant reads fail 404, stale
observations fail to mark telemetry live, and unknown commands are not
accepted.

## 5. Failure/chaos matrix

| Injected failure | Required outcome |
| --- | --- |
| API shutdown while virtual job running | Durable claim remains bounded; no auto-replay on restart |
| Pi/edge observation packet lost | No false job completion; telemetry becomes stale |
| Duplicate/late ACK | Idempotent exact duplicate or rejection; never a second execution |
| Two API replicas claim same lease | One authority winner; loser denied |
| Lease expires mid mission | Mission terminal `LEASE_LOST`; remaining jobs cancelled |
| Job ACK timeout | Mission terminal `FAILED_SIM`, no automatic requeue |
| JWT expired/wrong tenant/issuer/algorithm | Request rejected; no fallback anonymous path |
| PostgreSQL unavailable | Readiness fails; no acceptance of new operations |
| Region partition or scheduler outage | No second active command authority; on recovery only reconcile database state |

Neither a software receipt nor a simulated route is proof of a Pixhawk ACK,
physical motion, safe weather, airspace clearance or aircraft return-to-home.

## 6. Release gate and rollback

Before removing PR #24 draft status, require **all**:
- successful Python, PostgreSQL concurrency, and Docker/Compose CI for the
  exact reviewed SHA, with trustworthy run URLs and logs;
- a real OIDC provider/MFA, device credential revocation and verified
  transport trust, plus trusted host/private ingress testing;
- production-like Postgres migration, backup/restore, access control and
  two-replica stress/chaos tests with documented results;
- measurable SLOs, secrets handling, alert routing, on-call ownership,
  rollback to the previous API image, and a validated non-destructive
  schema migration strategy;
- SITL flight dynamics, independent aircraft safety review, HIL acceptance
  and qualified human authorization before *any separate physical UAV
  command adapter* is considered.

A production API rollback stops the API and scheduler, restores the previous
container image/config and preserves database schema/outbox evidence.
**Never downgrade the database destructively or replay unacknowledged
physical flight actions.**

## 7. Current evidence limitation

As of the reviewed feature branch, GitHub Actions has shown no cloud R1
runs/check results. The Python source and workflows have been committed but
are **not remotely test-verified**. Do not merge, deploy to Oracle, or
describe this as operational cloud flight control until the above gates pass.
