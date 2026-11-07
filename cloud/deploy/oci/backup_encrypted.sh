#!/usr/bin/env bash
# Off-host-key encrypted PostgreSQL snapshot; does not modify the database.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
if [[ -z "${COMPOSE_PROJECT_NAME:-}" ]]; then
  echo 'Set exact COMPOSE_PROJECT_NAME to avoid backing up wrong DB' >&2
  exit 1
fi
if [[ -z "${AEROHELM_AGE_RECIPIENT:-}" || -z "${AEROHELM_BACKUP_DIR:-}" ]]; then
  echo 'Set AEROHELM_AGE_RECIPIENT and AEROHELM_BACKUP_DIR' >&2
  exit 1
fi
command -v age >/dev/null || { echo 'age encryption CLI required' >&2; exit 1; }
command -v docker >/dev/null || { echo 'docker required' >&2; exit 1; }
umask 077
mkdir -p "$AEROHELM_BACKUP_DIR"
[[ ! -L "$AEROHELM_BACKUP_DIR" ]] || { echo 'Refusing symlinked backup directory' >&2; exit 1; }
outfile="$AEROHELM_BACKUP_DIR/aerohelm-$(date -u +%Y%m%dT%H%M%SZ)-${COMPOSE_PROJECT_NAME}.dump.age"
[[ ! -e "$outfile" ]] || { echo 'Refusing backup overwrite' >&2; exit 1; }
partial="${outfile}.partial"
trap 'rm -f "$partial"' EXIT
# pg_dump must run with the DBA on the isolated DB container; output is
# piped DIRECTLY into age encryption. Never create a plaintext SQL file.
docker compose -f cloud/compose.yaml -f cloud/compose.oci.yaml exec -T database \
  pg_dump -U postgres -d aerohelm -Fc \
  | age -r "$AEROHELM_AGE_RECIPIENT" -o "$partial"
[[ -s "$partial" ]] || { echo 'Encrypted backup empty' >&2; exit 1; }
mv "$partial" "$outfile"
trap - EXIT
sha256sum "$outfile" > "${outfile}.sha256"
echo 'Encrypted DB backup created; verify off-host restore before production.'
echo "Artifact: $outfile"
