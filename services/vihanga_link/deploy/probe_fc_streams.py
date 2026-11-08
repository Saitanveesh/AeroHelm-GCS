"""Operator-authenticated physical Pixhawk *stream-rate* command probe.

This is a genuine Oracle -> Pi -> FC MAV_CMD_SET_MESSAGE_INTERVAL request,
not a simulator. It cannot arm, change flight mode, upload missions, or take off.
A distinct disarmed-only opt-in is required on BOTH relay and Pi.
"""
from __future__ import annotations

import argparse
from getpass import getpass
import json
import time
from urllib.parse import urlsplit
from urllib.request import Request, build_opener, HTTPRedirectHandler


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def request_json(url: str, *, payload=None, token: str | None = None) -> dict:
    data = json.dumps(payload).encode() if payload is not None else None
    headers = {"Accept": "application/json"}
    if data is not None:
        headers["Content-Type"] = "application/json"
    if token is not None:
        headers["Authorization"] = "Bearer " + token
    request = Request(url, data=data, headers=headers,
                      method="POST" if data is not None else "GET")
    with build_opener(NoRedirect).open(request, timeout=6) as response:
        if response.geturl() != url:
            raise RuntimeError("Private origin redirected; refusing")
        return json.load(response)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--origin", required=True)
    parser.add_argument("--vehicle", required=True)
    parser.add_argument("--operator", default="operator")
    args = parser.parse_args()
    parsed = urlsplit(args.origin)
    if (parsed.scheme != "https" or not parsed.hostname or
        not parsed.hostname.endswith(".ts.net") or parsed.username or
        parsed.password or parsed.path not in ("", "/") or
        parsed.query or parsed.fragment):
        raise SystemExit("Use only approved private Tailscale HTTPS origin")
    if not args.vehicle or not args.vehicle.replace("_", "").replace("-", "").isalnum():
        raise SystemExit("Invalid aircraft ID")
    origin = args.origin.rstrip("/")
    print("This requests ONLY preconfigured volatile FC telemetry intervals.")
    print("Do not continue unless the FC is physically disarmed, propellers")
    print("are removed, the onboard pilot supervises the test and both")
    print("VIHANGA_* stream-probe feature flags were explicitly enabled.")
    answer = input("Type DISARMED-ON-SITE to continue: ")
    if answer != "DISARMED-ON-SITE":
        raise SystemExit("Cancelled before any request")
    secret = getpass("Private relay operator password (not echoed): ")
    result = request_json(origin + "/api/v1/auth/login", payload={
        "username": args.operator, "password": secret,
    })
    secret = None
    token = result["access_token"]
    before = request_json(origin + "/api/v1/mavlink/health")
    old_ack = before.get("message_ids", {}).get("77", 0)
    old_att = before.get("real_attitude_frames", 0)
    path = (origin + "/api/v1/vehicles/" + args.vehicle +
            "/telemetry/probe")
    queued = request_json(path, payload={}, token=token)
    print("Relay status:", queued.get("status"))
    print("Pi/FC acceptance is NOT implied by this queue receipt.")
    if queued.get("status") != "QUEUED_ON_PRIVATE_LINK":
        raise SystemExit("Relay did not accept fixed telemetry request")
    for _ in range(24):
        time.sleep(0.5)
        h = request_json(origin + "/api/v1/mavlink/health")
        physical = h.get("physical_stream_rate_command_ack", {})
        ack_new = h.get("message_ids", {}).get("77", 0) > old_ack
        attitude_new = h.get("real_attitude_frames", 0) > old_att
        if ack_new or attitude_new:
            print("Physical FC ID77 ACK observed:", ack_new)
            if ack_new:
                print("Latest FC MAV_RESULT:", physical.get("result"))
                print("Latest FC ACK accepted:", physical.get("accepted"))
            print("Fresh REAL ATTITUDE frames observed:", attitude_new)
            print("Native Quick feed complete:", h.get("native_quick_data_ready"))
            print("Pi intent status:", h.get("telemetry_probe", {}).get("status"))
            break
    else:
        print("No new real FC COMMAND_ACK or ATTITUDE in 12 seconds.")
        print("Inspect actual Pi serial logs, FC protocol/SYSID and stream rates.")
    print("No mode, arm, takeoff, mission or parameter-write command was sent.")


if __name__ == "__main__":
    main()
