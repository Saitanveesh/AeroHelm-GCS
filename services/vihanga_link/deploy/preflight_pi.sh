#!/usr/bin/env bash
# Read-only gate for one physical Pixhawk UART owner. Run AFTER the Pi reboot.
set -euo pipefail
device="${1:-/dev/ttyAMA10}"
if [[ "$device" != /dev/* ]] || [[ "$device" == *..* ]]; then
  echo "FAIL: expected an explicit /dev serial-device path" >&2
  exit 2
fi
if [[ ! -c "$device" ]]; then
  echo "FAIL: not a character device: $device" >&2
  exit 2
fi
real="$(readlink -f -- "$device")"
name="${real##*/}"
printf 'Pi hardware port: %s -> %s\n' "$device" "$real"
if [[ "$(id -u)" != 0 ]]; then
  echo "Run with sudo for reliable UART ownership evidence." >&2
  exit 2
fi
if grep -Eq '(^|[[:space:]])console=(serial0|'"$name"')([,[:space:]]|$)' /proc/cmdline; then
  echo "FAIL: Linux kernel console still owns this serial interface." >&2
  exit 2
fi
if grep -q 'systemd.debug_shell=1' /proc/cmdline; then
  echo "FAIL: remove the temporary unauthenticated debug-root boot option first." >&2
  exit 2
fi
if systemctl is-active --quiet "serial-getty@${name}.service"; then
  echo "FAIL: serial-getty is active on the selected MAVLink port." >&2
  exit 2
fi
if ! getent group dialout >/dev/null; then
  echo "FAIL: serial dialout group unavailable." >&2
  exit 2
fi
if ! id -nG vihanga 2>/dev/null | grep -qw dialout; then
  echo "FAIL: vihanga service account is not in dialout." >&2
  exit 2
fi
if command -v fuser >/dev/null; then
  if fuser -s "$real"; then
    echo "FAIL: UART already opened by another process:" >&2
    fuser -v "$real" >&2 || true
    exit 2
  fi
else
  echo "FAIL: fuser not installed; cannot verify sole ownership" >&2
  exit 2
fi
echo "PASS: no kernel serial console/getty, no debug root shell, no existing UART reader"
echo "PASS: dedicated vihanga account has dialout permissions"
echo "NEXT: start only the dedicated vihanga-pi.service after Oracle is healthy"
