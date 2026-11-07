"""Offline MAVLink relay and Pi stream-negotiation regression suite.

These tests use synthetic, CRC-valid packets and mock serial interfaces.
They never contact a Pixhawk or a production cloud instance.
"""
from __future__ import annotations

import hashlib
import struct
import time

from argon2 import PasswordHasher
from fastapi.testclient import TestClient
import pytest
from starlette.websockets import WebSocketDisconnect

from services.vihanga_link.relay import Config, create_app, envelope, valid_evidence
from services.vihanga_link.pi_agent import (
    Config as PiConfig, MESSAGE_RATES_HZ,
    is_disarmed_autopilot_heartbeat, request_streams_once,
    request_parameter_snapshot_once
)
from pymavlink import mavutil


def frame(message_id: int, payload: bytes, mav2: bool = False) -> bytes:
    header = bytearray((0xFD, len(payload), 0, 0, 1, 1, 1,
                        message_id & 255, message_id >> 8 & 255,
                        message_id >> 16 & 255)) if mav2 else bytearray(
                            (0xFE, len(payload), 1, 1, 1, message_id))
    assert message_id in (0, 22, 30)
    crc = 0xFFFF
    for value in bytes(header[1:]) + payload + bytes((
            {0: 50, 22: 220, 30: 39}[message_id],)):
        b = value ^ (crc & 255)
        b ^= b << 4 & 255
        crc = ((crc >> 8) ^ (b << 8) ^ (b << 3) ^ (b >> 4)) & 65535
    return bytes(header) + payload + struct.pack("<H", crc)


@pytest.fixture()
def credentials():
    device_token = "a" * 48 + "b" * 16
    config = Config(
        vehicle_id="AH-HW-001", operator_name="operator",
        operator_password_hash=PasswordHasher().hash("correct-bench-password"),
        jwt_key="C" * 48,
        device_token_sha256=hashlib.sha256(device_token.encode()).hexdigest(),
        allowed_hosts=("testserver",)
    )
    return config, device_token


def test_crc_evidence_is_real_and_no_false_heartbeat():
    hb = frame(0, bytes(9))
    att = frame(30, struct.pack("<Iff", 12, .3, -.4) + bytes(16), mav2=True)
    assert envelope(hb) == 0 and valid_evidence(hb, 0)
    assert envelope(att) == 30 and valid_evidence(att, 30)
    corrupt = bytearray(att)
    corrupt[12] ^= 2
    assert not valid_evidence(bytes(corrupt), 30)
    assert not valid_evidence(frame(0, bytes(9)), 30)
    assert envelope(b"\xfd\x04") is None
    assert envelope(b"invalid-data") is None


def test_no_implicit_or_default_credentials():
    with pytest.raises(ValueError):
        create_app(Config("AH-HW-001", "operator", "", "abc", "0" * 64,
                          ("testserver",)))
    with pytest.raises(ValueError):
        create_app(Config("AH-HW-001", "operator", "$argon2id$invalid",
                          "C" * 48, "0" * 64, ("*",)))


def test_full_raw_stream_viewer_and_live_evidence(credentials):
    cfg, device_token = credentials
    app = create_app(cfg)
    with TestClient(app) as client:
        assert client.post("/api/v1/auth/login", json={
            "username": "operator", "password": "wrong"
        }).status_code == 401
        login = client.post("/api/v1/auth/login", json={
            "username": "operator", "password": "correct-bench-password"
        })
        assert login.status_code == 200
        access = login.json()["access_token"]
        assert client.get("/health").json()["mode"] == "hardware-telemetry-only"
        with client.websocket_connect(
            "/ws/mavlink/view/AH-HW-001",
            headers={"Authorization": "Bearer " + access}) as viewer:
            with client.websocket_connect(
                "/ws/mavlink/device/AH-HW-001",
                headers={"Authorization": "Bearer " + device_token}) as device:
                hb = frame(0, bytes(9))
                att = frame(30, struct.pack("<Iff", 12, .15, .22) + bytes(16),
                            mav2=True)
                device.send_bytes(hb)
                device.send_bytes(att)
                assert viewer.receive_bytes() == hb
                assert viewer.receive_bytes() == att
                health = client.get("/api/v1/mavlink/health").json()
                assert health["device_online"]
                assert health["native_hud_heartbeat_ready"]
                assert health["attitude_stream_active"]
                assert health["real_attitude_frames"] == 1
                assert health["flight_control_enabled"] is False
                assert health["message_ids"]["30"] == 1


def test_authenticated_read_only_parameter_snapshot_and_epoch(credentials):
    cfg, device_token = credentials
    app = create_app(cfg)
    with TestClient(app) as client:
        raw = client.post("/api/v1/auth/login", json={
            "username": "operator", "password": "correct-bench-password"
        }).json()["access_token"]
        headers = {"Authorization": "Bearer " + raw}
        route = "/api/v1/vehicles/AH-HW-001/parameters"
        assert client.get(route).status_code == 401
        assert client.get(route, headers=headers).json()["status"] == "UNAVAILABLE"
        with client.websocket_connect("/ws/mavlink/device/AH-HW-001",
              headers={"Authorization": "Bearer " + device_token}) as device:
            for index in range(3):
                name = ("TEST_%d" % index).encode().ljust(16, b"\0")
                payload = struct.pack("<fHH", float(index), 3, index) + name + b"\x09"
                device.send_bytes(frame(22, payload))
            for _ in range(100):
                status = client.get(route, headers=headers).json()
                if status["verified_complete"]:
                    break
                time.sleep(0.01)
            assert status["verified_complete"]
            assert status["count"] == status["expected"] == 3
            assert status["epoch"] == 1
            assert status["read_only"]
            assert client.get("/api/v1/vehicles/OTHER/parameters",
                              headers=headers).status_code == 404
            assert client.get("/api/v1/mavlink/health").json()[
                "parameter_snapshot"]["status"] == "COMPLETE"
        assert client.get(route, headers=headers).json()["status"] == "UNAVAILABLE"


def test_non_attitude_mavlink_is_forwarded_unchanged(credentials):
    cfg, token = credentials
    codec = mavutil.mavlink.MAVLink(None, srcSystem=1, srcComponent=1)
    samples = (
        mavutil.mavlink.MAVLink_vfr_hud_message(
            1.0, 2.0, 180, 10, 12.5, 0.2),
        mavutil.mavlink.MAVLink_global_position_int_message(
            123, 125000000, 775000000, 180000, 5000, 1, 2, 3, 18000),
        mavutil.mavlink.MAVLink_gps_raw_int_message(
            123456, 3, 125000000, 775000000, 180000,
            100, 100, 10, 180, 10),
    )
    packets = [message.pack(codec) for message in samples]
    assert [envelope(p) for p in packets] == [74, 33, 24]
    with TestClient(create_app(cfg)) as client:
        jwt_token = client.post("/api/v1/auth/login", json={
            "username": "operator",
            "password": "correct-bench-password"}).json()["access_token"]
        with client.websocket_connect("/ws/mavlink/view/AH-HW-001",
              headers={"Authorization": "Bearer " + jwt_token}) as viewer:
            with client.websocket_connect("/ws/mavlink/device/AH-HW-001",
                  headers={"Authorization": "Bearer " + token}) as device:
                for packet in packets:
                    device.send_bytes(packet)
                    assert viewer.receive_bytes() == packet
                counts = client.get("/api/v1/mavlink/health").json()["message_ids"]
                assert counts["74"] == counts["33"] == counts["24"] == 1


def test_rejects_unauthorized_device_viewer_and_wrong_vehicle(credentials):
    cfg, device_token = credentials
    with TestClient(create_app(cfg)) as client:
        with pytest.raises(WebSocketDisconnect):
            with client.websocket_connect("/ws/mavlink/device/AH-HW-001",
                  headers={"Authorization": "Bearer wrong"}):
                pass
        with pytest.raises(WebSocketDisconnect):
            with client.websocket_connect("/ws/mavlink/view/AH-HW-001",
                  headers={"Authorization": "Bearer " + device_token}):
                pass
        with pytest.raises(WebSocketDisconnect):
            with client.websocket_connect("/ws/mavlink/device/OTHER",
                  headers={"Authorization": "Bearer " + device_token}):
                pass


def test_reconnect_expires_old_session_and_drops_stale_evidence(credentials):
    cfg, token = credentials
    app = create_app(cfg)
    with TestClient(app) as client:
        with client.websocket_connect("/ws/mavlink/device/AH-HW-001",
              headers={"Authorization": "Bearer " + token}) as device:
            device.send_bytes(frame(0, bytes(9)))
            # Wait for the producer to publish, then force the observation
            # timestamp old; avoid races between TestClient's ASGI portal and
            # this test thread.
            link = app.state.link
            for _ in range(100):
                if link.frames > 0:
                    break
                time.sleep(0.01)
            assert link.frames == 1
            link.last_heartbeat = time.monotonic() - 30
            assert not link.health(cfg.vehicle_id)["native_hud_heartbeat_ready"]
        h = client.get("/api/v1/mavlink/health").json()
        assert not h["device_online"]
        assert not h["native_hud_heartbeat_ready"]
        with client.websocket_connect("/ws/mavlink/device/AH-HW-001",
              headers={"Authorization": "Bearer " + token}):
            assert app.state.link.health(cfg.vehicle_id)["real_heartbeat_frames"] == 0
            assert app.state.link.health(cfg.vehicle_id)["epoch"] == 2


class Heartbeat:
    def __init__(self, armed=False, sysid=1):
        self.autopilot = mavutil.mavlink.MAV_AUTOPILOT_ARDUPILOTMEGA
        self.base_mode = mavutil.mavlink.MAV_MODE_FLAG_SAFETY_ARMED if armed else 0
        self.sysid = sysid

    def get_type(self): return "HEARTBEAT"
    def get_srcSystem(self): return self.sysid
    def get_srcComponent(self): return 1


class FakePort:
    class MAV:
        def __init__(self): self.commands = []
        def command_long_send(self, *args): self.commands.append(args)
        def param_request_list_send(self, *args): self.commands.append(("parameters",) + args)
    def __init__(self): self.mav = self.MAV()


def test_stream_requests_are_local_disarmed_and_nonpersistent(tmp_path):
    cfg = PiConfig("/dev/ttyAMA0", 57600, "AH-HW-001", 1,
                   "wss://pi-link.ts.net/ws/mavlink/device/AH-HW-001",
                   tmp_path / "device-secret", True, "pi-link.ts.net")
    cfg.validate()
    with pytest.raises(ValueError):
        PiConfig(cfg.device, cfg.baud, cfg.vehicle_id, cfg.system_id,
                 cfg.url, cfg.token_file, True, "unexpected.ts.net").validate()
    port = FakePort()
    assert not is_disarmed_autopilot_heartbeat(Heartbeat(True), 1)
    assert not request_streams_once(port, Heartbeat(True), cfg)
    assert not request_streams_once(port, Heartbeat(sysid=2), cfg)
    assert not port.mav.commands
    assert request_streams_once(port, Heartbeat(), cfg)
    assert len(port.mav.commands) == len(MESSAGE_RATES_HZ)
    for command in port.mav.commands:
        assert command[2] == mavutil.mavlink.MAV_CMD_SET_MESSAGE_INTERVAL
        assert command[0:2] == (1, 1)
    cfg_disabled = PiConfig(cfg.device, cfg.baud, cfg.vehicle_id, cfg.system_id,
                            cfg.url, cfg.token_file, False, cfg.pinned_relay_host)
    assert not request_streams_once(FakePort(), Heartbeat(), cfg_disabled)
    assert not request_parameter_snapshot_once(FakePort(), Heartbeat(), cfg_disabled)
    cfg_params = PiConfig(cfg.device, cfg.baud, cfg.vehicle_id, cfg.system_id,
                          cfg.url, cfg.token_file, True, cfg.pinned_relay_host, True)
    param_port = FakePort()
    assert not request_parameter_snapshot_once(param_port, Heartbeat(True), cfg_params)
    assert not request_parameter_snapshot_once(param_port, Heartbeat(sysid=4), cfg_params)
    assert param_port.mav.commands == []
    assert request_parameter_snapshot_once(param_port, Heartbeat(), cfg_params)
    assert param_port.mav.commands == [("parameters", 1, 1)]
