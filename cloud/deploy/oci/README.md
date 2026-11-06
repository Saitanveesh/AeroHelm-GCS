# Oracle Cloud private AeroHelm staging — NO aircraft control

This staging profile is for a **fresh isolated** Oracle Linux/Ubuntu VM
with Docker Compose v2 and the approved Tailscale host agent. It does
not expose a public HTTP port and is NOT a replacement for an existing
Oracle application until a migration/rollback plan is reviewed.

## Prerequisites

- Oracle host with SSH administration via the reviewed private mesh.
- Tailscale enrolled to the correct tailnet. Inbound grants only from
  operator/GCS and assigned Pi tags to HTTPS/443; no public Funnel.
- Private OIDC issuer with RS256 JWKS and signed `tenant`, `kind`,
  `scope`, `vehicle_id` claims. Device credentials provisioned out-of-band.
- Docker/Compose v2, PostgreSQL 17 image and approved encrypted off-host
  backups; explicit host firewall preventing 18080/public ingress.
- API DNS host (`AEROHELM_ALLOWED_HOSTS`) matching the real Tailscale
  HTTPS MagicDNS name and `AEROHELM_VIRTUAL_DISPATCH_ENABLED=false`.
- Existing production GCS/radio/FC installations stay unchanged.

## New data volume only

This stack uses separate *random* superuser bootstrap, DDL migrator, and
non-superuser app passwords. The DB init hook creates roles on the first
empty-volume boot only. Do not point the new stack at an existing
`aerohelm-postgres` volume; `POSTGRES_USER` changed compared with R1.

In a dedicated checkout and unique Compose project name, create credentials:

```bash
python3.12 -m cloud.deploy.oci.bootstrap_secrets
cp cloud/.env.example cloud/.env
# Edit cloud/.env to set the real issuer/JWKS/audience/allowed host.
# Keep AEROHELM_DATABASE_URL_FILE=/run/secrets/app_database_url.
export COMPOSE_PROJECT_NAME=aerohelm-cloud-r2
docker compose -f cloud/compose.yaml -f cloud/compose.oci.yaml config --quiet
```

Initialize only DB, then migrate as a **separate least-privileged migrator**:

```bash
docker compose -f cloud/compose.yaml -f cloud/compose.oci.yaml up -d database
docker compose -f cloud/compose.yaml -f cloud/compose.oci.yaml --profile migrate run --rm migrate
docker compose -f cloud/compose.yaml -f cloud/compose.oci.yaml up -d api scheduler compute
curl --fail --silent --show-error http://127.0.0.1:18080/health/ready
```

The `migrate` service is opt-in and is not started by normal `up`; it
reads `migrator_database_url`, which the runtime API/worker cannot read.
Migrations must be explicitly approved during maintenance. If you change
schema or image, take a tested snapshot and validate `alembic upgrade head`
against a disposable replica before promoting.

## Tailnet HTTPS ingress

After verifying the real node and that no other Tailscale Serve endpoint
owns port 443, use the *current* Tailscale Serve syntax (Tailscale >= 1.52):

```bash
sudo tailscale serve --bg --https=443 http://127.0.0.1:18080
sudo tailscale serve status
```

Your operator/Pi then reaches `https://<node>.<tailnet>.ts.net`, which
uses the tailnet's TLS certificate. Tailscale **Serve**, not Funnel.
Do NOT copy/paste these commands over an existing Serve configuration:
check `tailscale serve status` and the tailnet grants first.

Loopback mapping is `127.0.0.1:18080:8080` only. The DB, scheduler
and compute containers have no host-published ports. The API's internal
`edge` network is required for OIDC JWKS lookups; only API can use it.

## Non-destructive verification / rollback

- Confirm `/health/live` and `/health/ready`, authenticated
  `/api/v1/capabilities`, two-tenant denial, and virtual-only job flow.
- Validate app DB role is **NOSUPERUSER, NOCREATEDB, NOCREATEROLE**.
- Trigger virtual-only analytics and confirm the dedicated compute worker.
- Disconnect a test edge client and confirm telemetry stale while the
  physical GCS/RC backup remains independent.
- To roll back API image, restore the previous container image/config only
  after ensuring DB schema compatibility; NEVER destructively downgrade
  or delete mission/event evidence. Never auto-replay aircraft commands.
- Unpublishing the private Serve endpoint must use the current CLI and be
  reviewed against the existing tailnet configuration.

This is a source/configuration guide, **not confirmation of Oracle rollout,
OIDC credential provisioning, Pi pairing or flight acceptance**.
