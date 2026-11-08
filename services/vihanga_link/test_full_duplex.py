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
