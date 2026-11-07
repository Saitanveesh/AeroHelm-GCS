#!/usr/bin/env bash
# Explicit, supervised staging ONLY. Not a flight-control deployment.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-aerohelm-cloud-r2}"

files=(-f cloud/compose.yaml -f cloud/compose.oci.yaml)
if [[ "${1:-}" != "--apply" ]]; then
  printf '%s\n' 'No changes made. On the reviewed Oracle host, run:' \
    '  bash cloud/deploy/oci/deploy_staging.sh --apply' \
    'This creates a NEW PostgreSQL data volume and private cloud services.' \
    'First inspect running Compose projects and your tailnet Serve routes.'
  exit 0
fi

python3 -m cloud.deploy.oci.preflight
# Do not alter Tailscale Serve automatically; it may already be in use.
docker compose "${files[@]}" config --quiet
docker compose "${files[@]}" up -d database
docker compose "${files[@]}" --profile migrate run --rm migrate
docker compose "${files[@]}" up -d api scheduler compute

host="$(sed -n 's/^AEROHELM_ALLOWED_HOSTS=//p' cloud/.env | head -n 1 | cut -d, -f1)"
if [[ ! "$host" =~ ^[A-Za-z0-9][A-Za-z0-9.-]*$ ]]; then
  echo 'Invalid private Host header; refusing successful deployment claim' >&2
  exit 1
fi
bind_port="${AEROHELM_BIND_PORT:-18080}"
if [[ ! "$bind_port" =~ ^[0-9]{4,5}$ ]]; then
  echo 'Invalid local bind port' >&2
  exit 1
fi
curl --fail --silent --show-error --connect-timeout 3 --max-time 8 \
  -H "Host: $host" "http://127.0.0.1:${bind_port}/health/ready" >/dev/null
echo 'STAGING API ready on host loopback. Tailnet, OIDC and Pi acceptance pending.'
echo 'Inspect: sudo tailscale serve status ; do NOT enable public Funnel.'
