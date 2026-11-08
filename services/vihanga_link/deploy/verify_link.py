"""Live, receive-only verification of physical Pi -> Oracle -> operator packets.

Login is interactive; never print credentials or JWTs. This is not a flight
command client and cannot arm or move an aircraft.
"""
from __future__ import annotations

import argparse
import asyncio
from collections import Counter
from getpass import getpass
import json
from urllib.parse import urlsplit
from urllib.request import Request, build_opener, HTTPRedirectHandler

import websockets

from services.vihanga_link.relay import envelope

IMPORTANT = {0: "HEARTBEAT", 1: "SYS_STATUS", 22: "PARAM_VALUE",
             24: "GPS_RAW_INT", 30: "ATTITUDE", 33: "POSITION",
             74: "VFR_HUD", 77: "COMMAND_ACK", 147: "BATTERY_STATUS"}


async def inspect(base: str, vehicle: str, operator: str, seconds: float) -> None:
    parsed = urlsplit(base)
    if (parsed.scheme != "https" or not parsed.hostname or
        not parsed.hostname.endswith(".ts.net") or parsed.username or
        parsed.password or parsed.path not in ("", "/") or
        parsed.query or parsed.fragment):
        raise ValueError("Use only the approved private Tailscale HTTPS origin")
    password = getpass("Independent Vihanga relay password (not echoed): ")
    auth = json.dumps({"username": operator, "password": password}).encode()
    request = Request(base.rstrip("/") + "/api/v1/auth/login",
                      data=auth, headers={"Content-Type": "application/json"},
                      method="POST")
    class RejectRedirect(HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, headers, newurl):
            return None
    with build_opener(RejectRedirect).open(request, timeout=7) as response:
        if response.geturl() != request.full_url:
            raise RuntimeError("Login redirect not permitted")
        token = json.load(response)["access_token"]
    ws_url = "wss://" + parsed.netloc + "/ws/mavlink/view/" + vehicle
    counts: Counter[int] = Counter()
    deadline = asyncio.get_running_loop().time() + seconds
    async with websockets.connect(
            ws_url, additional_headers={"Authorization": "Bearer " + token},
            open_timeout=8, max_size=300) as ws:
        while asyncio.get_running_loop().time() < deadline:
            try:
                packet = await asyncio.wait_for(ws.recv(), timeout=2)
            except asyncio.TimeoutError:
                continue
            if not isinstance(packet, bytes):
                continue
            mid = envelope(packet)
            if mid is not None:
                counts[mid] += 1
    print("Real MAVLink frames observed in", seconds, "seconds:")
    for mid, label in IMPORTANT.items():
        print(f"  {mid:>3} {label:>16} {counts[mid]}")
    print("  Other IDs:", {str(k): v for k, v in sorted(counts.items())
                            if k not in IMPORTANT})
    if not counts[30]:
        print("BLOCKER: no real ATTITUDE (ID30); native HUD cannot move.")
    if not (counts[33] or counts[74]):
        print("BLOCKER: no verified position (ID33) or VFR_HUD (ID74).")
    print("This script sends NO flight-control commands.")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--origin", required=True,
                        help="e.g. https://aerohelm-vcn.tailf3ab6a.ts.net:8443")
    parser.add_argument("--vehicle", required=True)
    parser.add_argument("--operator", default="operator")
    parser.add_argument("--seconds", type=float, default=12.0)
    args = parser.parse_args()
    if not 2.0 <= args.seconds <= 60.0:
        parser.error("--seconds must be between 2 and 60")
    asyncio.run(inspect(args.origin, args.vehicle, args.operator, args.seconds))


if __name__ == "__main__":
    main()
