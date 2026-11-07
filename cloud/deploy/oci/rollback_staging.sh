#!/usr/bin/env bash
# Fail-closed staging stop. Does NOT drop volumes or downgrade schema.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
if [[ -z "${COMPOSE_PROJECT_NAME:-}" ]]; then
  echo 'Set exact COMPOSE_PROJECT_NAME before stopping any service' >&2
  exit 1
fi
if [[ "${1:-}" != "--stop-cloud-services" ]]; then
  echo 'Dry run. To stop only cloud API/scheduler/compute (keep database), use:'
  echo '  bash cloud/deploy/oci/rollback_staging.sh --stop-cloud-services'
  exit 0
fi
docker compose -f cloud/compose.yaml -f cloud/compose.oci.yaml \
  stop api scheduler compute
echo 'Cloud services stopped; database volume, audit and secrets preserved.'
echo 'Check tailnet Serve configuration separately; do not reset automatically.'
