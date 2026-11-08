"""Configure a new private, sole-owner Pi UART publisher.

Does not start the UART. Existing Pi owners must be identified and stopped
by the local operator before activating the new service.
"""
from __future__ import annotations

import hashlib
import os
from pathlib import Path
import pwd
import re
import secrets
import urllib.parse

from services.vihanga_link.oracle_identity import validate_instance_ocid

ENV = Path("/etc/vihanga/pi.env")
TOKEN = Path("/var/lib/vihanga/device-token")


def prompt(name: str, pattern: str) -> str:
    value = input(name + ": ").strip()
    if not re.fullmatch(pattern, value):
        raise SystemExit("Invalid " + name)
    return value


def main() -> None:
    if os.geteuid() != 0:
        raise SystemExit("Run with sudo")
    if ENV.exists() or TOKEN.exists():
        raise SystemExit("Existing Pi configuration/token. Refusing to rotate automatically.")
    serial = prompt("Observed Pixhawk serial device (/dev/...)", r"/dev/[A-Za-z0-9_./-]+")
    if ".." in serial or not Path(serial).exists():
        raise SystemExit("Serial device not present; verify the real Pi UART path")
    baud = int(prompt("Verified UART baud", r"[0-9]{4,6}"))
    if not 9600 <= baud <= 921600:
        raise SystemExit("Unsupported baud")
    vehicle = prompt("Aircraft ID (must match Oracle)", r"[A-Za-z0-9_-]{3,64}")
    sysid = int(prompt("Verified FC SYSID (1-254)", r"[0-9]{1,3}"))
    if not 1 <= sysid <= 254:
        raise SystemExit("FC SYSID out of range")
    host = prompt("Oracle private Tailscale hostname (without port)",
                  r"[A-Za-z0-9-]+(?:[.][A-Za-z0-9-]+)*[.]ts[.]net")
    ocid = input("Full Oracle Compute instance OCID (from its VM): ").strip()
    validate_instance_ocid(ocid)
    user = pwd.getpwnam("vihanga")
    url = "wss://" + host + ":8443/ws/mavlink/device/" + urllib.parse.quote(vehicle)
    secret = secrets.token_hex(48)
    TOKEN.parent.mkdir(parents=True, mode=0o700, exist_ok=True)
    fd = os.open(str(TOKEN), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="ascii") as f:
        f.write(secret)
    os.chown(TOKEN, user.pw_uid, user.pw_gid)
    pairs = {
        "VIHANGA_PI_SERIAL": serial,
        "VIHANGA_PI_BAUD": str(baud),
        "VIHANGA_VEHICLE_ID": vehicle,
        "VIHANGA_FC_SYSID": str(sysid),
        "VIHANGA_DEVICE_WS_URL": url,
        "VIHANGA_DEVICE_TOKEN_FILE": str(TOKEN),
        "VIHANGA_PINNED_RELAY_HOST": host,
        "VIHANGA_ORACLE_INSTANCE_OCID": ocid,
        "VIHANGA_DISARMED_STREAM_PROBE": "0",
        "VIHANGA_DISARMED_PARAM_SNAPSHOT": "0",
    }
    fd = os.open(str(ENV), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="ascii") as f:
        for key, value in pairs.items():
            f.write(key + "=" + value + "\n")
    print("Pi configured. Do not start while another process owns the serial.")
    print("Copy this random-token SHA256 digest to the Oracle relay configurator:")
    print(hashlib.sha256(secret.encode("ascii")).hexdigest())
    print("This digest is not the token; the token stays in the Pi's 0600 file.")
    print("No aircraft commands have been issued.")


if __name__ == "__main__":
    main()
