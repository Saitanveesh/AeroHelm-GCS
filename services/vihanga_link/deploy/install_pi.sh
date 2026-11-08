#!/usr/bin/env bash
# Install a separate Pi service but do NOT touch the old UART process.
set -euo pipefail
[[ "$(id -u)" == 0 ]] || { echo "Run with sudo" >&2; exit 1; }
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
[[ -f "$root/services/vihanga_link/pi_agent.py" ]] || {
  echo "Pi agent source missing from sparse checkout." >&2
  exit 1
}
if systemctl is-active --quiet vihanga-pi.service; then
  echo "Pi publisher already running. Do not replace while UART is active." >&2
  exit 1
fi
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y python3-venv python3-pip git psmisc
id -u vihanga >/dev/null 2>&1 ||
  useradd --system --user-group --home-dir /var/lib/vihanga \
    --shell /usr/sbin/nologin vihanga
getent group dialout >/dev/null ||
  { echo "No dialout group. Inspect serial-device permissions first." >&2; exit 1; }
usermod -aG dialout vihanga
install -d -m 0755 /opt/vihanga
install -d -o vihanga -g vihanga -m 0700 /var/lib/vihanga
install -d -m 0700 /etc/vihanga
if [[ ! -d /opt/vihanga/source/services/vihanga_link ]]; then
  echo "Expected sparse checkout at /opt/vihanga/source." >&2
  exit 1
fi
if [[ ! -x /opt/vihanga/venv/bin/python ]]; then
  python3 -m venv /opt/vihanga/venv
fi
/opt/vihanga/venv/bin/python -m pip install --no-cache-dir --prefer-binary \
  -r "$root/services/vihanga_link/requirements-pi.txt"
install -m 0644 "$root/services/vihanga_link/deploy/vihanga-pi.service" \
  /etc/systemd/system/vihanga-pi.service
systemctl daemon-reload
echo "Pi unit installed, not started. The previous UART owner is untouched."
echo "Next: sudo /opt/vihanga/venv/bin/python -m services.vihanga_link.deploy.configure_pi"
echo "Do not start vihanga-pi until the previous serial owner is identified and stopped."
