"""Offline MAVLink relay and Pi stream-negotiation regression suite.

These tests use synthetic, CRC-valid packets and mock serial interfaces.
They never contact a Pixhawk or a production cloud instance.
"""
from __future__ import annotations

import asyncio
import hashlib
import json
from collections import Counter
import struct
import time

from argon2 import PasswordHasher
from dataclasses import replace
from fastapi.testclient import TestClient
import pytest
from starlette.websockets import WebSocketDisconnect

from services.vihanga_link.relay import (Config, create_app, envelope, valid_evidence,
                                        valid_frame_crc, valid_native_attitude)
from services.vihanga_link.pi_agent import (
    Config as PiConfig, MESSAGE_RATES_HZ,
    is_disarmed_autopilot_heartbeat, request_streams_once,
    request_parameter_snapshot_once, observation_delta, stream_probe_due,
    Publisher as PiPublisher, verify_remote_oracle_instance
)
from pymavlink import mavutil


def frame(message_id: int, payload: bytes, mav2: bool = False,
          source_system: int = 1) -> bytes:
    header = bytearray((0xFD, len(payload), 0, 0, 1, source_system, 1,
                        message_id & 255, message_id >> 8 & 255,
                        message_id >> 16 & 255)) if mav2 else bytearray(
                            (0xFE, len(payload), 1, source_system, 1, message_id))
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




def test_crc_valid_but_invalid_attitude_cannot_mark_hud_live(credentials):
    cfg, token = credentials
    invalids = (
        frame(30, struct.pack("<Iff", 1, float("nan"), 0.2) + bytes(16)),
        frame(30, struct.pack("<Iff", 2, 0.1, float("inf")) + bytes(16),
              mav2=True),
        frame(30, struct.pack("<Iff", 3, 0.2, 2.0) + bytes(16),
              mav2=True),
    )
    for packet in invalids:
        assert valid_evidence(packet, 30)  # Intact CRC alone is insufficient.
        assert not valid_native_attitude(packet)
    genuine = frame(30, struct.pack("<Iff", 4, 0.2, -0.1) + bytes(16),
                    mav2=True)
    assert valid_native_attitude(genuine)

    with TestClient(create_app(cfg)) as client:
        with client.websocket_connect("/ws/mavlink/device/AH-HW-001",
              headers={"Authorization": "Bearer " + token}) as device:
            for packet in invalids:
                device.send_bytes(packet)
            device.send_bytes(genuine)
            for _ in range(100):
                health = client.get("/api/v1/mavlink/health").json()
                if health["frames"] + health["rejected_frames"] == 4:
                    break
                time.sleep(0.01)
            assert health["frames"] == 1
            assert health["real_attitude_frames"] == 1
            assert health["rejected_frames"] == 3
            assert health["attitude_stream_active"]


def test_foreign_sysid_is_rejected_even_with_valid_crc(credentials):
    cfg, token = credentials
    foreign_hb = frame(0, bytes(9), source_system=2)
    foreign_att = frame(30, struct.pack("<Iff", 12, .2, -.1) + bytes(16),
                        mav2=True, source_system=2)
    assert valid_evidence(foreign_hb, 0)
    assert valid_evidence(foreign_att, 30)
    app = create_app(cfg)
    with TestClient(app) as client:
        with client.websocket_connect("/ws/mavlink/device/AH-HW-001",
              headers={"Authorization": "Bearer " + token}) as device:
            device.send_bytes(foreign_hb)
            device.send_bytes(foreign_att)
            for _ in range(100):
                health = client.get("/api/v1/mavlink/health").json()
                if health["foreign_system_frames_rejected"] == 2:
                    break
                time.sleep(0.01)
            assert health["foreign_system_frames_rejected"] == 2
            assert health["rejected_frames"] == 2
            assert health["real_heartbeat_frames"] == 0
            assert health["real_attitude_frames"] == 0
            assert not health["native_hud_heartbeat_ready"]
            assert not health["attitude_stream_active"]

    # Configured SYSID 2 is accepted; no hardcoded assumption that FC is 1.
    alternate = Config(cfg.vehicle_id, cfg.operator_name,
                       cfg.operator_password_hash, cfg.jwt_key,
                       cfg.device_token_sha256, cfg.allowed_hosts, 2)
    with TestClient(create_app(alternate)) as client:
        with client.websocket_connect("/ws/mavlink/device/AH-HW-001",
              headers={"Authorization": "Bearer " + token}) as device:
            device.send_bytes(foreign_hb)
            for _ in range(100):
                health = client.get("/api/v1/mavlink/health").json()
                if health["real_heartbeat_frames"]:
                    break
                time.sleep(0.01)
            assert health["real_heartbeat_frames"] == 1
            assert health["foreign_system_frames_rejected"] == 0


def test_pi_refuses_wrong_oracle_instance_before_device_auth(tmp_path):
    ocid = "ocid1.instance.oc1.ap-hyderabad-1." + "a" * 48
    cfg = PiConfig(
        "/dev/ttyAMA0", 57600, "AH-HW-001", 1,
        "wss://new-relay.ts.net/ws/mavlink/device/AH-HW-001",
        tmp_path / "token", True, "new-relay.ts.net",
        oracle_instance_ocid=ocid,
    )
    cfg.validate()
    expected = hashlib.sha256(ocid.encode()).hexdigest()
    assert verify_remote_oracle_instance(cfg, fetch=lambda: {
        "oracle_instance": {
            "configured": True, "verified": True,
            "instance_sha256": expected
        }
    })
    with pytest.raises(RuntimeError, match="not bound"):
        verify_remote_oracle_instance(cfg, fetch=lambda: {
            "oracle_instance": {
                "configured": True, "verified": True,
                "instance_sha256": hashlib.sha256(b"old-vm").hexdigest()
            }
        })
    with pytest.raises(RuntimeError, match="not bound"):
        verify_remote_oracle_instance(cfg, fetch=lambda: {"status": "ok"})


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
                assert not health["native_quick_data_ready"]
                assert health["flight_data_streams"]["0"]["fresh"]
                assert health["flight_data_streams"]["30"]["fresh"]
                assert not health["flight_data_streams"]["33"]["fresh"]
                codec = mavutil.mavlink.MAVLink(None, srcSystem=1, srcComponent=1)
                position = mavutil.mavlink.MAVLink_global_position_int_message(
                    123, 125000000, 775000000, 180000, 5000, 1, 2, 3, 18000).pack(codec)
                device.send_bytes(position)
                assert viewer.receive_bytes() == position
                health = client.get("/api/v1/mavlink/health").json()
                assert health["native_quick_data_ready"]
                assert health["flight_data_streams"]["33"]["frames"] == 1
                assert isinstance(health["frame_age_ms"], int)
                assert isinstance(health["heartbeat_age_ms"], int)
                assert isinstance(health["attitude_age_ms"], int)
                assert health["frame_rate_5s_hz"] > 0
                assert health["heartbeat_rate_5s_hz"] > 0
                assert health["attitude_rate_5s_hz"] > 0
                assert health["flight_control_enabled"] is False
                assert health["message_ids"]["30"] == 1


def test_authenticated_telemetry_request_is_bounded_and_pi_acks_without_flight_control(credentials):
    cfg, device_token = credentials
    route = "/api/v1/vehicles/AH-HW-001/telemetry/probe"
    allowed_cfg = replace(cfg, allow_disarmed_telemetry_requests=True)
    app = create_app(allowed_cfg)
    with TestClient(app) as client:
        logged = client.post("/api/v1/auth/login", json={
            "username": "operator", "password": "correct-bench-password"
        }).json()["access_token"]
        bearer = {"Authorization": "Bearer " + logged}
        assert client.post(route).status_code == 401
        assert client.post(route, headers=bearer).status_code == 409
        assert client.post("/api/v1/vehicles/OTHER/telemetry/probe",
                           headers=bearer).status_code == 404
        with client.websocket_connect("/ws/mavlink/device/AH-HW-001",
              headers={"Authorization": "Bearer " + device_token}) as pi:
            assert client.post(route, headers=bearer).status_code == 409
            pi.send_bytes(frame(0, bytes(9)))
            for _ in range(100):
                if app.state.link.health(cfg.vehicle_id)["native_hud_heartbeat_ready"]:
                    break
                time.sleep(0.01)
            assert app.state.link.health(cfg.vehicle_id)["native_hud_heartbeat_ready"]
            # A viewer can never inject a raw MAVLink command into the Pi.
            with client.websocket_connect("/ws/mavlink/view/AH-HW-001",
                    headers=bearer) as viewer:
                viewer.send_bytes(b"arbitrary gcs flight commands are discarded")
                assert client.get("/api/v1/mavlink/health").json()[
                    "telemetry_probe"]["request_count"] == 0
            response = client.post(route, headers=bearer)
            assert response.status_code == 200
            details = response.json()
            assert details["status"] == "QUEUED_ON_PRIVATE_LINK"
            assert details["fc_stream_confirmed"] is False
            assert details["flight_control_enabled"] is False
            request = pi.receive_json()
            assert request == {
                "type": "disarmed_stream_probe",
                "vehicle_id": "AH-HW-001",
                "request_id": details["request_id"],
            }
            assert client.post(route, headers=bearer).status_code == 429
            pi.send_text(json.dumps({
                "type": "disarmed_stream_probe_ack",
                "request_id": details["request_id"],
                "status": "QUEUED",
                "fc_stream_confirmed": False,
            }))
            for _ in range(100):
                health = client.get("/api/v1/mavlink/health").json()
                if health["telemetry_probe"]["status"] == "QUEUED":
                    break
                time.sleep(0.01)
            assert health["telemetry_probe"]["status"] == "QUEUED"
            assert health["real_attitude_frames"] == 0
            assert not health["attitude_stream_active"]
            assert health["flight_control_enabled"] is False
        assert client.get("/api/v1/mavlink/health").json()[
            "telemetry_probe"]["status"] == "NOT_REQUESTED"

    disabled = create_app(cfg)
    with TestClient(disabled) as client:
        token = client.post("/api/v1/auth/login", json={
            "username": "operator", "password": "correct-bench-password"
        }).json()["access_token"]
        assert client.post(route, headers={"Authorization": "Bearer " + token}
                           ).status_code == 403


def test_pi_disarmed_stream_request_rejects_armed_stale_disabled_and_arbitrary_commands(tmp_path):
    cfg = PiConfig("/dev/ttyAMA0", 57600, "AH-HW-001", 1,
                   "wss://pi-link.ts.net/ws/mavlink/device/AH-HW-001",
                   tmp_path / "token", True, "pi-link.ts.net")
    payload = {"type": "disarmed_stream_probe", "vehicle_id": cfg.vehicle_id,
               "request_id": "f" * 32}
    publisher = PiPublisher(cfg)
    assert publisher.queue_disarmed_telemetry_request(
        {"type": "arm", "vehicle_id": cfg.vehicle_id,
         "request_id": "f" * 32}) is None
    assert publisher.queue_disarmed_telemetry_request(
        {**payload, "vehicle_id": "OTHER"}) is None
    assert publisher.queue_disarmed_telemetry_request(payload)[
        "status"] == "REFUSED_DISARM_REQUIRED"
    publisher._last_disarmed_heartbeat = time.monotonic()
    assert publisher.queue_disarmed_telemetry_request(payload)[
        "status"] == "QUEUED"
    assert publisher._remote_probe_pending.is_set()
    assert publisher.queue_disarmed_telemetry_request(payload)[
        "status"] == "REFUSED_RATE_LIMIT"
    publisher._remote_probe_pending.clear()
    publisher._seen_armed = True
    assert publisher.queue_disarmed_telemetry_request(payload)[
        "status"] == "REFUSED_DISARM_REQUIRED"
    publisher._seen_armed = False
    publisher._last_disarmed_heartbeat = time.monotonic() - 5
    assert publisher.queue_disarmed_telemetry_request(payload)[
        "status"] == "REFUSED_DISARM_REQUIRED"
    blocked = PiPublisher(replace(cfg, stream_probe=False))
    blocked._last_disarmed_heartbeat = time.monotonic()
    assert blocked.queue_disarmed_telemetry_request(payload)[
        "status"] == "REFUSED_DISABLED"


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
        assert client.get(route + "/values", headers=headers).status_code == 409
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
            values = client.get(route + "/values", headers=headers)
            assert values.status_code == 200
            rows = values.json()
            assert rows["writable"] is False
            assert rows["aircraft_commands_enabled"] is False
            assert rows["values"]["TEST_1"]["float32"] == 1.0
            assert rows["values"]["TEST_1"]["type"] == 9
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



def test_crc_enforced_for_position_and_vfr_hud_before_relay(credentials):
    """All flight-data IDs need X25 verification, not only HB/ATTITUDE."""
    cfg, token = credentials
    codec = mavutil.mavlink.MAVLink(None, srcSystem=1, srcComponent=1)
    messages = (
        mavutil.mavlink.MAVLink_global_position_int_message(
            123, 125000000, 775000000, 180000, 5000, 1, 2, 3, 18000),
        mavutil.mavlink.MAVLink_vfr_hud_message(
            1.0, 2.0, 180, 10, 12.5, 0.2),
    )
    originals = [message.pack(codec) for message in messages]
    damaged = []
    for packet in originals:
        assert valid_frame_crc(packet)
        payload_offset = 6 if packet[0] == 0xFE else 10
        bad = bytearray(packet)
        bad[payload_offset] ^= 1
        assert envelope(bytes(bad)) == envelope(packet)
        assert not valid_frame_crc(bytes(bad))
        damaged.append(bytes(bad))

    app = create_app(cfg)
    with TestClient(app) as client:
        with client.websocket_connect("/ws/mavlink/device/AH-HW-001",
              headers={"Authorization": "Bearer " + token}) as device:
            for good, corrupt in zip(originals, damaged):
                device.send_bytes(good)
                device.send_bytes(corrupt)
            for _ in range(100):
                health = client.get("/api/v1/mavlink/health").json()
                if health["frames"] + health["rejected_frames"] == 4:
                    break
                time.sleep(0.01)
            assert health["frames"] == 2
            assert health["rejected_frames"] == 2
            assert health["message_ids"] == {"33": 1, "74": 1}
            assert health["real_attitude_frames"] == 0
            assert not health["attitude_stream_active"]


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



def test_handshake_failure_releases_publisher_and_viewer_slots(credentials):
    cfg, device_token = credentials
    app = create_app(cfg)

    class BrokenHandshake:
        def __init__(self, token):
            self.headers = {"authorization": "Bearer " + token}
            self.closed = False
        async def accept(self):
            raise OSError("simulated handshake interrupted")
        async def close(self, code=1000):
            self.closed = True

    routes = {route.path: route.endpoint for route in app.routes
              if hasattr(route, "path")}
    device = BrokenHandshake(device_token)
    with pytest.raises(OSError, match="handshake interrupted"):
        asyncio.run(routes["/ws/mavlink/device/{vehicle_id}"](
            device, cfg.vehicle_id))
    assert not app.state.link.connected
    assert app.state.link.health(cfg.vehicle_id)["device_online"] is False

    with TestClient(app) as client:
        access = client.post("/api/v1/auth/login", json={
            "username": "operator", "password": "correct-bench-password"
        }).json()["access_token"]
    viewer = BrokenHandshake(access)
    asyncio.run(routes["/ws/mavlink/view/{vehicle_id}"](
        viewer, cfg.vehicle_id))
    assert not app.state.link.viewers
    assert viewer.closed


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
        assert not h["native_quick_data_ready"]
        assert not h["flight_data_streams"]["33"]["fresh"]
        assert h["frame_age_ms"] is None
        assert h["heartbeat_age_ms"] is None
        assert h["attitude_age_ms"] is None
        assert h["frame_rate_5s_hz"] == 0
        assert h["heartbeat_rate_5s_hz"] == 0
        assert h["attitude_rate_5s_hz"] == 0
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




def test_pi_uart_reader_never_queues_foreign_aircraft_frames(tmp_path):
    cfg = PiConfig("/dev/ttyAMA0", 57600, "AH-HW-001", 1,
                   "wss://pi-link.ts.net/ws/mavlink/device/AH-HW-001",
                   tmp_path / "device-secret", False, "pi-link.ts.net")
    local = frame(0, bytes(9), source_system=1)
    other = frame(0, bytes(9), source_system=2)
    other_att = frame(30, struct.pack("<Iff", 1, 0.4, 0.2) + bytes(16),
                      mav2=True, source_system=2)

    class FakeMessage:
        def __init__(self, raw, system_id):
            self.raw = raw
            self.system_id = system_id
            self.base_mode = 0
            self.autopilot = mavutil.mavlink.MAV_AUTOPILOT_ARDUPILOTMEGA
        def get_type(self): return "HEARTBEAT" if self.raw[0] == 0xFE else "ATTITUDE"
        def get_msgId(self): return 0 if self.raw[0] == 0xFE else 30
        def get_srcSystem(self): return self.system_id
        def get_srcComponent(self): return 1
        def get_msgbuf(self): return self.raw

    class FakeSerial:
        def __init__(self, messages, stop):
            self.messages = list(messages)
            self.stop = stop
        def recv_match(self, **kwargs):
            if self.messages:
                return self.messages.pop(0)
            self.stop.set()
            return None

    class InlineLoop:
        def call_soon_threadsafe(self, fn, *args): fn(*args)

    pi = PiPublisher(cfg)
    pi.port = FakeSerial((FakeMessage(other, 2), FakeMessage(other_att, 2),
                          FakeMessage(local, 1)), pi.stop)
    pi._loop = InlineLoop()
    pi._read_uart()
    assert pi.foreign_system_dropped == 2
    assert pi.counts[0] == 1
    assert pi.counts[30] == 0
    assert pi.queue.qsize() == 1
    assert pi.queue.get_nowait()[1] == local


def test_pi_window_counts_do_not_infer_missing_attitude():
    old = Counter({0: 12, 30: 2, 33: 4, 74: 5})
    now = Counter({0: 23, 30: 2, 33: 15, 74: 7, 24: 1})
    delta = observation_delta(now, old)
    assert delta[0] == 11
    assert delta[30] == 0
    assert delta[33] == 11
    assert delta[74] == 2
    assert delta[24] == 1
    assert delta[1] == delta[147] == 0
    assert observation_delta(Counter(), old)[0] == 0


def test_private_probe_issues_only_predefined_disarmed_local_rates(tmp_path):
    cfg = PiConfig("/dev/ttyAMA0", 57600, "AH-HW-001", 1,
                   "wss://pi-link.ts.net/ws/mavlink/device/AH-HW-001",
                   tmp_path / "token", True, "pi-link.ts.net")
    pi = PiPublisher(cfg)
    hb = frame(0, bytes(9))

    class FullHeartbeat(Heartbeat):
        def get_msgId(self): return 0
        def get_msgbuf(self): return hb

    class SingleHeartbeatPort(FakePort):
        def __init__(self):
            super().__init__()
            self.read_count = 0
        def recv_match(self, **kwargs):
            self.read_count += 1
            if self.read_count == 1:
                return FullHeartbeat()
            pi.stop.set()
            return None

    class InlineLoop:
        def call_soon_threadsafe(self, fn, *args): fn(*args)

    pi.port = SingleHeartbeatPort()
    pi._loop = InlineLoop()
    pi._last_disarmed_heartbeat = time.monotonic()
    payload = {"type": "disarmed_stream_probe", "vehicle_id": cfg.vehicle_id,
               "request_id": "a" * 32}
    assert pi.queue_disarmed_telemetry_request(payload)["status"] == "QUEUED"
    pi._read_uart()
    assert not pi._remote_probe_pending.is_set()
    assert len(pi.port.mav.commands) == len(MESSAGE_RATES_HZ)
    assert all(cmd[2] == mavutil.mavlink.MAV_CMD_SET_MESSAGE_INTERVAL
               for cmd in pi.port.mav.commands)
    assert pi.counts[0] == 1 and pi.counts[30] == 0
    assert pi.queue.qsize() == 1


def test_stream_requests_are_local_disarmed_and_nonpersistent(tmp_path):
    cfg = PiConfig("/dev/ttyAMA0", 57600, "AH-HW-001", 1,
                   "wss://pi-link.ts.net/ws/mavlink/device/AH-HW-001",
                   tmp_path / "device-secret", True, "pi-link.ts.net")
    cfg.validate()
    assert stream_probe_due(cfg, seen_armed=False, attitude_seen=False,
                            attempts=0, seconds_since_last_probe=0)
    assert not stream_probe_due(cfg, seen_armed=False, attitude_seen=False,
                                attempts=1, seconds_since_last_probe=14.0)
    assert stream_probe_due(cfg, seen_armed=False, attitude_seen=False,
                            attempts=1, seconds_since_last_probe=15.0)
    assert not stream_probe_due(cfg, seen_armed=True, attitude_seen=False,
                                attempts=1, seconds_since_last_probe=100.0)
    assert not stream_probe_due(cfg, seen_armed=False, attitude_seen=True,
                                attempts=1, seconds_since_last_probe=100.0)
    assert not stream_probe_due(cfg, seen_armed=False, attitude_seen=False,
                                attempts=2, seconds_since_last_probe=100.0)
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
