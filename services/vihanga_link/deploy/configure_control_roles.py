"""Provision independent pilot/approver credentials for simulation-only API.

This does not enable physical aircraft control. It never changes Pi configs,
UART ownership, hardware interlocks or the Mission Planner control bridge.
"""
from __future__ import annotations

from getpass import getpass
import hashlib
import hmac
import os
from pathlib import Path
import re
import secrets
import sys

from argon2 import PasswordHasher

DEST = Path("/etc/vihanga/control.env")


def read_role(role: str) -> tuple[str, str, str]:
    username = input(role + " username: ").strip()
    if not re.fullmatch(r"[A-Za-z0-9_-]{3,64}", username):
        raise SystemExit("Invalid " + role + " username")
    password = getpass(role + " password (at least 16 characters): ")
    repeat = getpass("Repeat " + role + " password: ")
    if not secrets.compare_digest(password, repeat) or len(password) < 16:
        raise SystemExit("Passwords differ or are too short")
    # Only a transient comparison digest is retained to reject reuse
    # across independent roles. No password or comparison digest is printed.
    reuse_digest = hashlib.sha256(password.encode("utf-8")).hexdigest()
    result = PasswordHasher().hash(password)
    del password, repeat
    return username, result, reuse_digest


def main() -> None:
    if os.geteuid() != 0:
        raise SystemExit("Run on Oracle VM with sudo")
    if DEST.exists():
        raise SystemExit("Control credential file exists. Refusing to overwrite/rotate")
    print("Independent SIMULATOR-ONLY control roles. No physical flight commands.")
    pilot_name, pilot_hash, pilot_compare = read_role("Pilot")
    approver_name, approver_hash, approver_compare = read_role("Safety approver")
    if (pilot_name == approver_name or
        hmac.compare_digest(pilot_compare, approver_compare)):
        raise SystemExit("Distinct human identities and credentials required")
    del pilot_compare, approver_compare
    enabled = input("Type SIMULATOR to enable ONLY simulator authorization: ").strip()
    if enabled != "SIMULATOR":
        raise SystemExit("Not enabled")
    values = {
        "VIHANGA_CONTROL_SIMULATOR_ONLY": "1",
        "VIHANGA_CONTROL_PILOT_NAME": pilot_name,
        "VIHANGA_CONTROL_PILOT_PASSWORD_HASH": pilot_hash,
        "VIHANGA_CONTROL_APPROVER_NAME": approver_name,
        "VIHANGA_CONTROL_APPROVER_PASSWORD_HASH": approver_hash,
        "VIHANGA_CONTROL_JWT_KEY": secrets.token_urlsafe(48),
    }
    DEST.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd = os.open(str(DEST), os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        with os.fdopen(fd, "w", encoding="ascii") as output:
            for name, value in values.items():
                output.write(name + "=" + value + "\n")
        os.chmod(DEST, 0o600)
    except BaseException:
        DEST.unlink(missing_ok=True)
        raise
    print("Created root-only simulator credential file:", DEST)
    print("No physical Pi command dispatch or flight authority enabled.")


if __name__ == "__main__":
    main()
