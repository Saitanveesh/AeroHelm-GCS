#!/usr/bin/env bash
# Install package and service unit only. Does not replace the existing
# aerohelm-api.service, Tailscale :443, or any existing credentials.
set -euo pipefail
[[ "$(id -u)" == 0 ]] || { echo "Run with sudo" >&2; exit 1; }
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
[[ -f "$root/services/vihanga_link/relay.py" ]] || {
  echo "Run from a sparse checkout containing services/vihanga_link" >&2
  exit 1
}
if systemctl is-active --quiet vihanga-relay.service; then
  echo "Vihanga relay already active; stop it explicitly before upgrading." >&2
  exit 1
fi
if ss -ltnH 'sport = :8765' | grep -q .; then
  echo "TCP 8765 is occupied. No service changes performed." >&2
  exit 1
fi
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y python3-venv python3-pip git
id -u vihanga >/dev/null 2>&1 ||
  useradd --system --user-group --home-dir /var/lib/vihanga \
    --shell /usr/sbin/nologin vihanga
install -d -m 0755 /opt/vihanga /var/lib/vihanga
install -d -m 0700 /etc/vihanga
if [[ ! -d /opt/vihanga/source/services/vihanga_link ]]; then
  echo "Expected sparse Git checkout at /opt/vihanga/source." >&2
  echo "Use the documented clone command; do not copy an unrelated directory." >&2
  exit 1
fi
if [[ ! -x /opt/vihanga/venv/bin/python ]]; then
  python3 -m venv /opt/vihanga/venv
fi
/opt/vihanga/venv/bin/python -m pip install --no-cache-dir --prefer-binary \
  -r "$root/services/vihanga_link/requirements-cloud.txt"
install -m 0644 "$root/services/vihanga_link/deploy/vihanga-relay.service" \
  /etc/systemd/system/vihanga-relay.service
systemctl daemon-reload
echo "Installed separate Oracle relay unit and Python requirements."
echo "Next: sudo /opt/vihanga/venv/bin/python -m services.vihanga_link.deploy.configure_relay"
echo "Then: sudo systemctl enable --now vihanga-relay.service"
echo "Only then: sudo tailscale serve --bg --https=8443 http://127.0.0.1:8765"
echo "Existing aerohelm-api :8000 / Tailscale :443 unchanged."
