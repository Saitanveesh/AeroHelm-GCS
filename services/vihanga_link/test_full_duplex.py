"""Offline private-link ping checks; no serial device is opened."""

from pathlib import Path

from services.vihanga_link.pi_agent import Config, Publisher


def test_ping_reply_never_opens_uart(tmp_path: Path):
    cfg = Config(
        "/dev/ttyAMA10", 57600, "AH-HW-001", 1,
        "wss://pi-link.ts.net/ws/mavlink/device/AH-HW-001",
        tmp_path / "token", False, "pi-link.ts.net",
    )
    publisher = Publisher(cfg)
    data = {"type": "link_ping", "vehicle_id": cfg.vehicle_id,
            "request_id": "a" * 32}
    reply = publisher.transport_ping_reply(data)
    assert reply["type"] == "link_pong"
    assert reply["request_id"] == data["request_id"]
    assert reply["flight_control_enabled"] is False
    assert publisher.port is None
    assert publisher.transport_ping_reply({**data, "vehicle_id": "OTHER"}) is None
    assert publisher.transport_ping_reply({**data, "request_id": "invalid"}) is None
    assert publisher.transport_ping_reply({**data, "type": "unknown"}) is None


def test_cloud_to_pi_ping_requires_login_and_matching_pong():
    import hashlib
    import time
    from argon2 import PasswordHasher
    from fastapi.testclient import TestClient
    from services.vihanga_link.relay import Config as RelayConfig, create_app

    device_token = "local-test-" + "e" * 64
    config = RelayConfig(
        vehicle_id="AH-HW-001", operator_name="operator",
        operator_password_hash=PasswordHasher().hash("local-only-password"),
        jwt_key="k" * 48,
        device_token_sha256=hashlib.sha256(device_token.encode()).hexdigest(),
        allowed_hosts=("testserver",),
    )
    app = create_app(config)
    endpoint = "/api/v1/vehicles/AH-HW-001/link/ping"
    with TestClient(app) as client:
        assert client.post(endpoint).status_code == 401
        token = client.post("/api/v1/auth/login", json={
            "username": "operator", "password": "local-only-password"
        }).json()["access_token"]
        headers = {"Authorization": "Bearer " + token}
        assert client.post(endpoint, headers=headers).status_code == 409
        with client.websocket_connect(
                "/ws/mavlink/device/AH-HW-001",
                headers={"Authorization": "Bearer " + device_token}) as device:
            queued = client.post(endpoint, headers=headers)
            assert queued.status_code == 200
            request = device.receive_json()
            assert request["type"] == "link_ping"
            assert request["request_id"] == queued.json()["request_id"]
            assert queued.json()["flight_controller_contacted"] is False
            device.send_json({
                "type": "link_pong", "vehicle_id": "WRONG",
                "request_id": request["request_id"],
                "flight_control_enabled": False,
            })
            assert client.get("/api/v1/mavlink/health").json()[
                "transport_ping"]["round_trip_verified"] is False
            device.send_json({
                "type": "link_pong", "vehicle_id": "AH-HW-001",
                "request_id": request["request_id"],
                "flight_control_enabled": False,
            })
            for _ in range(100):
                state = client.get("/api/v1/mavlink/health").json()
                if state["transport_ping"]["round_trip_verified"]:
                    break
                time.sleep(0.01)
            assert state["transport_ping"]["status"] == "PONG_FROM_PI"
            assert state["transport_ping"]["rtt_ms"] >= 0
            assert state["transport_ping"]["flight_controller_contacted"] is False
            assert state["flight_control_enabled"] is False
        state = client.get("/api/v1/mavlink/health").json()
        assert not state["transport_ping"]["round_trip_verified"]
