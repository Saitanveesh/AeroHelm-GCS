# AeroHelm Cloud R1 — Production promotion / safety and SRE acceptance

**Single source repository:** `Saitanveesh/AeroHelm-GCS`.
**Source branch:** `feat/cloud-fleet-control-plane-r1` (cloud-only changes from
`master`). Windows GCS / Mission Planner plugin scope is paused.

## Demonstrated source capability versus missing evidence

| Domain | Current source | Evidence still required |
| --- | --- | --- |
| Durable tenant mission registry | SQLAlchemy PostgreSQL schema + Alembic | Real migration/backup/restore and 2-replica conflict test |
| Multi-UAV task orchestration | Deterministic allocator; bounded task queue | Formal operational flight feasibility, fleet capacity benchmarks |
| Identity | Production RS256 OIDC issuer/audience/JWKS; scoped device JWT | External IdP setup, MFA, tenant claim trust, mTLS and revocation |
| Pilot/vehicle authority | Two-person approval, SHA256 plan, per-vehicle leases/epochs | Cross-node stress and failover fencing verification |
| Cloud-edge protocol | Virtual-only job claim/ACK, expiry, replay inbox | SITL/HIL adapter, physical ack semantics, shutdown-on-loss assurance |
| State and audit | Transactional outbox and monotonic telemetry sessions | Event broker/export, immutable archive, retention and alerting |
| Network | Private-only reference Compose | Gateway deployment, latency/loss tests, TLS key management |
| Recovery | Scheduler terminal-expiry/no auto requeue | 5-30 s disconnect chaos, restart, failover, backup recovery |
| Aircraft safety | Physical dispatch **disabled in code** | Separate safety case, manual pilot, FC failsafes, legal flight approvals |

## Hard blocks before production operation

1. CI must execute and pass the **exact PR head SHA**. The workflow is
   `.github/workflows/aerohelm-cloud-control-plane.yml`. If Actions are
   not running for the repo, record that as a release blocker; do not
   interpret a committed workflow as a passing test.
2. Run PostgreSQL migrations in an isolated environment; verify schema pin,
   connection pooling, transactional isolation and unique constraints.
   Test simultaneous claim, lease acquisition, mission start and device
   event ID duplication across at least two API processes. Document
   deadlocks and retry handling without re-sending physical commands.
3. Configure a real OIDC IdP and trust policy: verify wrong issuer/audience,
   expired tokens, non-RS256 algorithm, wrong tenant and operator/device
   privilege separation; enforce MFA for privileged operations.
4. Provision database and device credentials using a secret manager;
   require TLS1.3 at trusted ingress, strict mesh ACLs, egress restrictions,
   validated cert rotations, controlled admin recovery and DDoS limits.
   Never expose PostgreSQL or the Pi UART to the Internet.
5. Exercise snapshot/restore of mission, job, lease, ACK inbox, and outbox.
   Test rollback to a previous container image without deleting schema
   or replaying previously dispatched jobs.
6. Run the virtual edge and SITL mission scenarios with packet loss,
   delayed ACKs, repeated ACKs, rejected ACKs, clock skew, API crash,
   scheduler crash, database crash and stale telemetry. Any uncertain
   execution state must be `UNKNOWN/FAILED` and never retried
   automatically against a physical aircraft.
7. Add audited deployment approvals, infrastructure metrics, alerting,
   error budgets, SLOs for telemetry freshness and job claim latency,
   resource budgets, and an on-call rollback playbook.
8. Only after independent safety engineering sign-off should a **separate
   PR** propose a real hardware command adapter, with onboard flight
   termination and pilot takeover verified in SITL/HIL/propeller-off
   tests and the operating jurisdiction's legal requirements.

## Engineering signoff

The code in this branch is a cloud backend **release candidate**, not a
certified, deployed, or production-accepted UAV flight control system.
There is deliberately **no** operation to arm a drone, upload a physical
mission, change a Pixhawk mode, calibrate, or send arbitrary MAVLink.

The principal failure to avoid: **a cloud accepted HTTP request is not a
flight-controller ACK**. A dual-region, multi-UAV platform must never
equate cloud consensus, WebSocket connectivity or simulation completion
with physical flight authorization. On a partition, onboard failsafes and
manual pilot authority remain independently functional.
