# AeroHelm — Thin-GCS / cloud compute contract v1

**Authoritative repository:** `Saitanveesh/AeroHelm-GCS`.
**Code branch:** `feat/cloud-analytics-offload-r1` stacked on the cloud R1
control-plane branch. This is a source-only candidate, not Oracle deployment.

## Resource ownership

The native Mission Planner GCS retains only real-time *local* functions:
native HUD rendering, direct/independent telemetry radio, link selection,
local pilot control, and a last-known-status display. None of those can be
made dependent on cloud uptime, and no changes to those native components
are included in this cloud-only feature.

Heavy or growing work moves to cloud-backed services: per-tenant fleet
metrics, missions/history aggregation, advisory telemetry quality, alert
analysis and eventual sensor/video inference. The GCS should request
summary results by API and render a small status widget; it must not download
all historical samples or perform portfolio-wide joins.

## V1 cloud workload protocol

An authenticated operator with `analytics:submit` role POSTs to
`/api/v1/cloud-compute/jobs`:

```json
{"request_id":"0123456789abcdef0123456789abcdef","kind":"fleet_health"}
```

Supported `kind` is exactly `fleet_health` or `mission_portfolio`.
There is no generic `execute`, MAVLink downlink, shell, script, or file
upload. Server returns HTTP 202 with a durable UUID job ID, `QUEUED`
status and `physical_control_enabled=false`. The same principal can
retry exactly the same request ID/kind without scheduling duplicate work;
reusing that ID for another kind returns 409.

The client, with `fleet:read`, polls
`GET /api/v1/cloud-compute/jobs/<uuid>` for `QUEUED`, `SUCCEEDED`
or `FAILED`, and renders at most the returned bounded JSON report.
Another operator or tenant gets 404. No JWT, device token, or raw video
appears in stored reports. Report `generated_at` uses server time.

* `fleet_health`: up to 128 tenant vehicles, fresh/stale telemetry,
  device-reported low battery/GNSS advisory flags, simulated vs unverified
  observation provenance, and total at-risk count. No flight-ready verdict.
* `mission_portfolio`: grouped mission and virtual-job counts from
  PostgreSQL, no raw route samples or mission command payloads.

PostgreSQL stores submissions. A separate `cloud.app.compute_worker`
claims queued reports with `FOR UPDATE SKIP LOCKED` and commits the
result/audit event atomically. Workers can be scaled independently after
testing. A crash before commit safely leaves a **read-only analysis** in
queue for recomputation, unlike an unsafe uncertain aircraft command.

Bounded quotas: at most 16 outstanding reports and 512 retained reports
per tenant. At quota exhaustion, request fails 429; it never pushes
computation to the GCS. Archive/pruning must be implemented and reviewed
before quota exhaustion becomes an operational problem.

## Infrastructure, size and isolation

The `cloud/Dockerfile` includes only `cloud/app`, database migrations
and Alembic configuration. Runtime dependencies are pinned separately in
`cloud/requirements.txt`; `cloud/requirements-dev.txt` adds pytest and
HTTP test libraries for CI only. Mission Planner source, mobile SDK builds,
test models, and data-plane artifacts do not enter the cloud container.

The private Compose configuration provides:
- DB on an internal-only bridge
- API on DB bridge plus reviewed private IdP/gateway egress
- scheduler on the DB bridge only
- compute worker on the DB bridge only

No new host-published port or Pi listener is needed. Analytics runs behind
the existing JWT and signed tenant identity boundaries.

## Pending acceptance gates

Unit and adversarial API tests; PostgreSQL v0001→v0002 migration on
disposable DB; two-worker racing claims; API restart/result persistence;
Docker image size/import and private network policy smoke; OIDC roles;
production secrets and database restore. GitHub Actions must run on the
exact head SHA before merge. A real Windows GCS cloud analytics widget is
**not** built in this batch; the established native GCS and backups remain
unchanged.

**No physical UAV command path exists in these compute workloads.**
