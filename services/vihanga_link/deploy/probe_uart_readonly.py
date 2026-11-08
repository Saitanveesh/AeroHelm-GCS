"""Read-only physical Pi UART diagnostic. NEVER transmits data to the FC.

Run only with vihanga-pi.service stopped, on a disarmed, propellers-removed
bench, with an explicit --device and --baud. This prints received byte counts
and actual parsed MAVLink IDs without guessing the UART wiring or baud.
"""
from __future__ import annotations

import argparse
from collections import Counter
import os
from pathlib import Path
import subprocess
import time

from pymavlink.dialects.v20 import ardupilotmega as mavlink_dialect
import serial


def ensure_exclusive(device: str) -> str:
    if not device.startswith("/dev/") or ".." in device or not Path(device).is_char_device():
        raise ValueError("Choose a present /dev character device")
    actual = os.path.realpath(device)
    active = subprocess.run(
        ["systemctl", "is-active", "--quiet", "vihanga-pi.service"],
        check=False, timeout=4,
    )
    if active.returncode == 0:
        raise RuntimeError("Stop vihanga-pi.service before read-only UART probing")
    owners = subprocess.run(["fuser", "-s", actual], check=False, timeout=4)
    if owners.returncode == 0:
        raise RuntimeError("Selected UART is already opened; stop its verified owner first")
    if owners.returncode != 1:
        raise RuntimeError("Cannot verify physical UART ownership via fuser")
    return actual


def sample(device: str, baud: int, seconds: float) -> dict:
    if baud < 9600 or baud > 921600:
        raise ValueError("Baud must be between 9600 and 921600")
    if not 2 <= seconds <= 20:
        raise ValueError("Sample duration must be 2–20 seconds")
    actual = ensure_exclusive(device)
    parser = mavlink_dialect.MAVLink(None)
    parser.robust_parsing = True
    ids: Counter[int] = Counter()
    sysids: Counter[int] = Counter()
    bytes_received = 0
    v1_marks = 0
    v2_marks = 0
    bad_data = 0
    end = time.monotonic() + seconds
    # pyserial's write() is NEVER called; no MAVLink requests or commands.
    with serial.Serial(actual, baudrate=baud, timeout=0.3, exclusive=True) as port:
        while time.monotonic() < end:
            data = port.read(512)
            bytes_received += len(data)
            v1_marks += data.count(b"\xfe")
            v2_marks += data.count(b"\xfd")
            for b in data:
                try:
                    message = parser.parse_char(bytes((b,)))
                except (ValueError, IndexError, TypeError, mavlink_dialect.MAVError):
                    bad_data += 1
                    continue
                if message is None:
                    continue
                if message.get_type() == "BAD_DATA":
                    bad_data += 1
                    continue
                ids[int(message.get_msgId())] += 1
                sysids[int(message.get_srcSystem())] += 1
    result = {
        "physical_device": actual,
        "baud": baud,
        "raw_bytes": bytes_received,
        "mavlink_v1_markers": v1_marks,
        "mavlink_v2_markers": v2_marks,
        "valid_mavlink_messages": sum(ids.values()),
        "message_ids": dict(sorted(ids.items())),
        "source_system_ids": dict(sorted(sysids.items())),
        "malformed_chunks": bad_data,
    }
    return result


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--device", required=True)
    p.add_argument("--baud", required=True, type=int)
    p.add_argument("--seconds", type=float, default=8)
    args = p.parse_args()
    try:
        report = sample(args.device, args.baud, args.seconds)
    except (RuntimeError, ValueError, OSError, serial.SerialException) as ex:
        p.exit(2, f"UART PROBE BLOCKED: {ex}\n")
    for key, value in report.items():
        print(f"{key}: {value}")
    if not report["raw_bytes"]:
        print("RESULT: no UART RX bytes observed; verify physical pins/FC power/port.")
    elif not report["valid_mavlink_messages"]:
        print("RESULT: bytes present but no valid MAVLink; verify baud and protocol.")
    else:
        print("RESULT: valid physical MAVLink. Use this UART and baud in pi.env.")
    print("READ-ONLY. No FC commands or UART writes were issued.")


if __name__ == "__main__":
    main()
