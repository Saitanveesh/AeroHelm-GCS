"""Physical Vihanga deployment acceptance: FC -> cloud, cloud -> Pi, cloud -> FC.

Default is read-only except a private, authenticated Pi transport ping.
The optional FC test requests ONLY fixed volatile MAVLink stream intervals
(command 511) on an identified disarmed, propellers-removed bench. No flight
modes, arming, takeoff, navigation, or parameter writes are supported.
"""
from __future__ import annotations

import argparse
import asyncio
from collections import Counter
from getpass import getpass
import hashlib
import json
import re
import time
from urllib.parse import urlsplit

import websockets
from services.vihanga_link.deploy.probe_fc_streams import request_json
from services.vihanga_link.oracle_identity import validate_instance_ocid
from services.vihanga_link.relay import envelope, valid_frame_crc

IMPORTANT = {0: "HEARTBEAT", 30: "ATTITUDE", 33: "GLOBAL_POSITION_INT",
             74: "VFR_HUD", 1: "SYS_STATUS", 24: "GPS_RAW_INT", 77: "COMMAND_ACK"}


def validate_origin(origin: str, ocid: str, vehicle: str) -> str:
    validate_instance_ocid(ocid)
    target = urlsplit(origin)
    if (target.scheme != "https" or not target.hostname or
            not target.hostname.endswith(".ts.net") or target.port != 8443 or
            target.username or target.password or target.path not in ("", "/") or
            target.query or target.fragment):
        raise ValueError("Require pinned tailnet HTTPS :8443 origin (no path or redirects)")
    if not re.fullmatch(r"[A-Za-z0-9_-]{3,64}", vehicle):
        raise ValueError("Invalid aircraft ID")
    return origin.rstrip("/")


def check_relay(origin: str, expected_ocid: str) -> None:
    status = request_json(origin + "/health")
    binding = status.get("oracle_instance", {})
    expected_hash = hashlib.sha256(expected_ocid.encode("ascii")).hexdigest()
    if (status.get("service") != "vihanga-private-relay" or
            not isinstance(binding, dict) or
            binding.get("configured") is not True or
            binding.get("verified") is not True or
            binding.get("instance_sha256") != expected_hash):
        raise RuntimeError("Origin is not the identity-pinned production Vihanga relay")
    print("PASS: private TLS origin and exact Oracle Compute identity")


def login(origin: str, username: str) -> str:
    password = getpass("Vihanga relay operator password (not echoed): ")
    try:
        body = request_json(origin + "/api/v1/auth/login",
                            payload={"username": username, "password": password})
    finally:
        password = None
    token = body.get("access_token")
    if not isinstance(token, str) or not token:
        raise RuntimeError("Relay login did not return a JWT")
    return token


def health(origin: str, vehicle: str) -> dict:
    state = request_json(origin + "/api/v1/mavlink/health")
    if state.get("vehicle_id") != vehicle:
        raise RuntimeError("Unexpected physical aircraft ID from relay")
    return state


def transport_ping(origin: str, vehicle: str, token: str) -> int:
    path = origin + "/api/v1/vehicles/" + vehicle + "/link/ping"
    sent = request_json(path, payload={}, token=token)
    request_id = sent.get("request_id")
    if sent.get("status") != "WAITING_FOR_PI" or not re.fullmatch(
            r"[0-9a-f]{32}", str(request_id)):
        raise RuntimeError("Relay refused to queue the private Pi ping")
    deadline = time.monotonic() + 7.0
    while time.monotonic() < deadline:
        time.sleep(0.2)
        ping = health(origin, vehicle).get("transport_ping", {})
        if (ping.get("request_id") == request_id and
                ping.get("round_trip_verified") is True and
                ping.get("status") == "PONG_FROM_PI" and
                ping.get("flight_controller_contacted") is False):
            rtt = ping.get("rtt_ms")
            print(f"PASS: Oracle -> Pi -> Oracle WebSocket pong, round trip {rtt} ms")
            print("NOTE: Pi ping does not contact or command the Pixhawk.")
            return int(rtt)
    raise RuntimeError("No matching Pi pong after 7 seconds; full-duplex WSS not verified")


async def observe_real_packets(origin: str, vehicle: str, token: str,
                               duration: float) -> Counter[int]:
    target = urlsplit(origin)
    url = "wss://" + target.netloc + "/ws/mavlink/view/" + vehicle
    seen: Counter[int] = Counter()
    end = asyncio.get_running_loop().time() + duration
    async with websockets.connect(
            url, additional_headers={"Authorization": "Bearer " + token},
            max_size=300, open_timeout=9, ping_interval=15) as ws:
        while asyncio.get_running_loop().time() < end:
            try:
                payload = await asyncio.wait_for(ws.recv(), timeout=min(
                    1.5, max(0.01, end - asyncio.get_running_loop().time())))
            except asyncio.TimeoutError:
                continue
            if isinstance(payload, bytes) and valid_frame_crc(payload):
                mid = envelope(payload)
                if mid is not None:
                    seen[mid] += 1
    print("Physical MAVLink frames received via cloud viewer:")
    for mid, title in IMPORTANT.items():
        print(f"  {title:20s} ID {mid:3d}: {seen[mid]}")
    if seen[0] == 0:
        raise RuntimeError("No real flight-controller HEARTBEAT received at operator")
    if seen[30] == 0:
        print("BLOCKER: no real ATTITUDE ID30; HUD cannot be validated.")
    if seen[33] == 0 and seen[74] == 0:
        print("BLOCKER: no real GLOBAL_POSITION_INT ID33 or VFR_HUD ID74.")
    print("PASS: real flight-controller heartbeat delivered from Pi to operator")
    return seen


def fc_probe(origin: str, vehicle: str, token: str) -> None:
    print("BENCH COMMAND: requests predefined volatile stream intervals only.")
    print("Required: aircraft physically disarmed; propellers removed;")
    print("on-site operator present; both stream-probe opt-in flags enabled.")
    if input("Type PROPS-REMOVED-DISARMED to authorize: ") != "PROPS-REMOVED-DISARMED":
        raise RuntimeError("Bench command cancelled")
    before = health(origin, vehicle)
    if not (before.get("device_online") and
            before.get("native_hud_heartbeat_ready")):
        raise RuntimeError("No fresh physical FC HEARTBEAT; aborting command")
    old_epoch = before["epoch"]
    old_ack = before["physical_stream_rate_command_ack"]["frames"]
    old_att = before["real_attitude_frames"]
    old_pos = before["flight_data_streams"]["33"]["frames"]
    old_hud = before["flight_data_streams"]["74"]["frames"]
    path = origin + "/api/v1/vehicles/" + vehicle + "/telemetry/probe"
    result = request_json(path, payload={}, token=token)
    req_id = result.get("request_id")
    if result.get("status") != "QUEUED_ON_PRIVATE_LINK":
        raise RuntimeError("Relay did not queue the fixed FC probe")
    print("Relay accepted request for delivery, not proof of physical execution.")
    deadline = time.monotonic() + 14.0
    pi_accepted = False
    ack_accepted = False
    streamed = False
    while time.monotonic() < deadline:
        time.sleep(0.35)
        state = health(origin, vehicle)
        if not state.get("device_online") or state.get("epoch") != old_epoch:
            raise RuntimeError("Device reconnected during bench test; proof invalid")
        probe = state.get("telemetry_probe", {})
        if probe.get("request_id") != req_id:
            raise RuntimeError("Another probe superseded this request")
        if str(probe.get("status", "")).startswith("REFUSED"):
            raise RuntimeError("Pi refused physical probe: " + probe["status"])
        pi_accepted = probe.get("status") == "QUEUED"
        physical = state.get("physical_stream_rate_command_ack", {})
        ack_accepted = (physical.get("frames", 0) > old_ack and
                        physical.get("accepted") is True and
                        physical.get("result") == 0)
        streamed = (state.get("real_attitude_frames", 0) > old_att and
                    (state["flight_data_streams"]["33"]["frames"] > old_pos or
                     state["flight_data_streams"]["74"]["frames"] > old_hud))
        if pi_accepted and ack_accepted and streamed:
            break
    print("Pi returned request ACK:", pi_accepted)
    print("New physical FC COMMAND_ACK for MAV_CMD 511 accepted:", ack_accepted)
    print("New FC-generated ID30 plus ID33 or ID74 observed:", streamed)
    if not pi_accepted:
        raise RuntimeError("Pi did not acknowledge the diagnostic request")
    if not ack_accepted:
        raise RuntimeError("FC acceptance not proven by a new COMMAND_ACK")
    if not streamed:
        raise RuntimeError("FC accepted interval request but complete data did not arrive")
    print("PASS: Oracle command -> Pi -> physical FC ACK -> verified FC data")
    print("No arming, GUIDED, takeoff, waypoint, or parameter write was issued.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--origin", required=True)
    parser.add_argument("--vehicle", required=True)
    parser.add_argument("--oracle-ocid", required=True,
                        help="Exact full Oracle Compute instance OCID")
    parser.add_argument("--operator", default="operator")
    parser.add_argument("--seconds", type=float, default=10.0)
    parser.add_argument("--bench-fc-probe", action="store_true",
                        help="Opt-in physical disarmed FC command 511 test")
    args = parser.parse_args()
    if not 3 <= args.seconds <= 45:
        parser.error("--seconds must be between 3 and 45")
    try:
        origin = validate_origin(args.origin, args.oracle_ocid, args.vehicle)
        check_relay(origin, args.oracle_ocid)
        token = login(origin, args.operator)
        transport_ping(origin, args.vehicle, token)
        asyncio.run(observe_real_packets(origin, args.vehicle, token, args.seconds))
        if args.bench_fc_probe:
            fc_probe(origin, args.vehicle, token)
        else:
            print("FC command path NOT TESTED (add --bench-fc-probe on a supervised disarmed bench).")
        print("ACCEPTANCE FINISHED: only observed stages are reported as validated.")
    except (RuntimeError, ValueError, OSError, KeyError, TypeError) as ex:
        parser.exit(status=2, message=f"VALIDATION FAILED: {ex}\n")


if __name__ == "__main__":
    main()
