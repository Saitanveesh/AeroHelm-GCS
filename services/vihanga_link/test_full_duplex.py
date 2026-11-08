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


def test_idle_device_does_not_disconnect_after_legacy_twelve_seconds():
    """An unpowered FC must not force endless :8443 Pi WSS reconnects."""
    import hashlib
    import time
    from argon2 import PasswordHasher
    from fastapi.testclient import TestClient
    from services.vihanga_link.relay import Config as RelayConfig, create_app

    token = "test-only-" + "x" * 48
    cfg = RelayConfig(
        vehicle_id="AH-HW-001", operator_name="operator",
        operator_password_hash=PasswordHasher().hash("offline-test-only"),
        jwt_key="J" * 48,
        device_token_sha256=hashlib.sha256(token.encode()).hexdigest(),
        allowed_hosts=("testserver",),
    )
    with TestClient(create_app(cfg)) as client:
        with client.websocket_connect(
                "/ws/mavlink/device/AH-HW-001",
                headers={"Authorization": "Bearer " + token}) as device:
            initial = client.get("/api/v1/mavlink/health").json()
            assert initial["device_online"]
            assert initial["frames"] == 0
            time.sleep(13.0)
            result = client.get("/api/v1/mavlink/health").json()
            assert result["device_online"], "Silent Pi was forcibly disconnected"
            assert result["epoch"] == initial["epoch"]
            assert result["frames"] == 0
            # Transport controls can be sent over a live, otherwise idle WSS.
            auth = client.post("/api/v1/auth/login", json={
                "username": "operator", "password": "offline-test-only"
            }).json()["access_token"]
            request = client.post(
                "/api/v1/vehicles/AH-HW-001/link/ping",
                headers={"Authorization": "Bearer " + auth},
            )
            assert request.status_code == 200
            delivered = device.receive_json()
            assert delivered["type"] == "link_ping"
            assert delivered["request_id"] == request.json()["request_id"]


def test_readonly_probe_rejects_active_vihanga_service(monkeypatch):
    import subprocess
    import pytest
    from services.vihanga_link.deploy import probe_uart_readonly as diag

    def fake_run(cmd, **_kwargs):
        assert cmd[:3] == ["systemctl", "is-active", "--quiet"]
        return subprocess.CompletedProcess(cmd, 0)
    monkeypatch.setattr(diag.subprocess, "run", fake_run)
    with pytest.raises(RuntimeError, match="Stop vihanga-pi"):
        diag.ensure_exclusive("/dev/null")


def test_pi_stop_during_idle_uart_wait_exits_cleanly(monkeypatch, tmp_path):
    """A deliberate systemd stop must not be reported as UART reader failure."""
    import asyncio
    from services.vihanga_link import pi_agent as module

    cfg = module.Config(
        "/dev/ttyAMA0", 57600, "AH-HW-001", 1,
        "wss://pi-link.ts.net/ws/mavlink/device/AH-HW-001",
        tmp_path / "token", False, "pi-link.ts.net",
    )
    pub = module.Publisher(cfg)

    class OldThread:
        def is_alive(self):
            return False

    pub._uart_thread = OldThread()

    class Reader:
        def done(self):
            return False

    async def fake_timeout(awaitable, *, timeout):
        awaitable.close()
        pub.stop.set()
        raise asyncio.TimeoutError()

    monkeypatch.setattr(module.asyncio, "wait_for", fake_timeout)
    asyncio.run(pub._publish_over_ws(None, Reader()))
    assert pub._uart_failure is None
