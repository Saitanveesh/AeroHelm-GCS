"""Configure the separate Oracle Vihanga relay without printing credentials.

Run with sudo from the repository root after installing the service venv.
The generated systemd EnvironmentFile is root-only and never checked into Git.
"""
from __future__ import annotations

from getpass import getpass
import hashlib
import os
from pathlib import Path
import re
import secrets
import sys
from urllib.request import Request, urlopen
import json

from argon2 import PasswordHasher
from services.vihanga_link.oracle_identity import METADATA_URL, validate_instance_ocid

DEST = Path("/etc/vihanga/relay.env")


def prompt(label: str, pattern: str) -> str:
    value = input(label + ": ").strip()
    if not re.fullmatch(pattern, value):
        raise SystemExit("Invalid " + label + ". Configuration not written.")
    return value


def metadata_id() -> str:
    request = Request(METADATA_URL, headers={"Authorization": "Bearer Oracle"})
    with urlopen(request, timeout=5) as response:
        if response.geturl() != METADATA_URL:
            raise RuntimeError("OCI instance metadata redirected")
        body = response.read(16385)
        if len(body) > 16384:
            raise RuntimeError("OCI instance metadata too large")
    value = json.loads(body)["id"]
    validate_instance_ocid(value)
    return value


def main() -> None:
    if os.geteuid() != 0:
        raise SystemExit("Run configuration with sudo")
    if DEST.exists():
        raise SystemExit("Relay configuration exists. Refusing to overwrite its secrets.")
    instance = metadata_id()
    print("Oracle instance verified locally; SHA256 suffix:",
          hashlib.sha256(instance.encode()).hexdigest()[-12:])
    hostname = prompt("Private Tailscale hostname (without port)",
                      r"[A-Za-z0-9-]+(?:[.][A-Za-z0-9-]+)*[.]ts[.]net")
    vehicle = prompt("Physical aircraft ID", r"[A-Za-z0-9_-]{3,64}")
    fc_sysid = int(prompt("Verified FC SYSID (1-254)", r"[0-9]{1,3}"))
    if not 1 <= fc_sysid <= 254:
        raise SystemExit("FC SYSID out of range")
    digest = prompt("SHA256 digest of Pi's random device token (64 hex)",
                    r"[a-fA-F0-9]{64}").lower()
    username = prompt("Relay operator login name", r"[A-Za-z0-9_-]{3,64}")
    password = getpass("New independent relay operator password: ")
    confirmation = getpass("Repeat relay password: ")
    if password != confirmation or len(password) < 16:
        raise SystemExit("Passwords differ or are shorter than 16 characters")
    password_hash = PasswordHasher().hash(password)
    key = secrets.token_urlsafe(48)
    # Keep each value on one line for the systemd EnvironmentFile parser.
    pairs = {
        "VIHANGA_VEHICLE_ID": vehicle,
        "VIHANGA_FC_SYSID": str(fc_sysid),
        "VIHANGA_ALLOWED_HOSTS": "localhost,127.0.0.1," + hostname,
        "VIHANGA_ORACLE_INSTANCE_OCID": instance,
        "VIHANGA_OPERATOR_NAME": username,
        "VIHANGA_OPERATOR_PASSWORD_HASH": password_hash,
        "VIHANGA_JWT_KEY": key,
        "VIHANGA_DEVICE_TOKEN_SHA256": digest,
        "VIHANGA_ALLOW_DISARMED_STREAM_REQUEST": "0",
    }
    content = "".join(k + "=" + v + "\n" for k, v in pairs.items())
    DEST.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    fd = os.open(str(DEST), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(content)
    except BaseException:
        DEST.unlink(missing_ok=True)
        raise
    finally:
        password = confirmation = None
    print("Private relay configured at", DEST)
    print("The original aerohelm-api.service and HTTPS :443 are unchanged.")
    print("Stream probes default to disabled until a verified disarmed bench.")
    print("No device token or password has been printed.")


if __name__ == "__main__":
    main()
