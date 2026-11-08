"""Offline Oracle↔Pi shadow-control-path tests; absolutely no UART actuation."""
from __future__ import annotations

import asyncio
import json
import time
from types import SimpleNamespace
from unittest.mock import AsyncMock

from fastapi.testclient import TestClient

from services.vihanga_link.control_transport_probe import (
    ControlTransportProbe, NO_ACTUATION, pi_probe_reply,
)
from services.vihanga_link.relay import create_app
from services.vihanga_link.test_control_api import (
    VEHICLE, make_configs, authenticated_roles,
)


def _valid_challenge():
    return {
        "type": "control_transport_probe", "vehicle_id": VEHICLE,
        "request_id": "a" * 32, "control_epoch": 7,
        "challenge": "b" * 32,
    }


def test_pi_shadow_response_is_fixed_and_never_calls_uart():
    expected = {
        **_valid_challenge(), "type": "control_transport_probe_ack",
        "status": NO_ACTUATION, "uart_touched": False,
        "fc_ack_received": False, "flight_control_enabled": False,
    }
    assert pi_probe_reply(_valid_challenge(), VEHICLE) == expected
    for change in (
        {"type": "COMMAND_LONG"},
        {"action": "ARM"},
        {"params": {"force": 1}},
        {"mavlink": "anything"},
        {"control_epoch": True},
        {"control_epoch": -1},
        {"control_epoch": 2**63},
        {"challenge": "not-a-challenge"},
        {"request_id": "xyz"},
        {"vehicle_id": "OTHER"},
    ):
        assert pi_probe_reply({**_valid_challenge(), **change}, VEHICLE) is None
    assert pi_probe_reply(None, VEHICLE) is None
    assert pi_probe_reply("raw mavlink", VEHICLE) is None


def test_role_separation_and_real_authenticated_pi_shadow_roundtrip():
    relay_cfg, control_cfg, device_token = make_configs()
    path = f"/api/v1/vehicles/{VEHICLE}/control/transport/probe"
    with TestClient(create_app(relay_cfg, control_cfg)) as client:
        pilot, approver = authenticated_roles(client)
        pilot_headers = {"Authorization": "Bearer " + pilot}
        approver_headers = {"Authorization": "Bearer " + approver}
        telemetry_token = client.post("/api/v1/auth/login", json={
            "username": "telemetry", "password": "telemetry-test-pass",
        }).json()["access_token"]
        assert client.post(path).status_code == 403
        assert client.post(path, headers=approver_headers).status_code == 401
        assert client.post(path, headers={"Authorization": "Bearer " + telemetry_token}).status_code == 401
        assert client.post(path, headers=pilot_headers).status_code == 409
        assert client.post(path.replace(VEHICLE, "OTHER"), headers=pilot_headers).status_code == 404
        assert client.get("/api/v1/flight-control/status").json()["physical_command_dispatch"] is False

        with client.websocket_connect(
            f"/ws/mavlink/device/{VEHICLE}",
            headers={"Authorization": "Bearer " + device_token},
        ) as pi:
            # Any request body is ignored; only a fixed, non-actuating
            # challenge is sent, even if a client attempts a command payload.
            response = client.post(path, headers=pilot_headers,
                                   json={"action": "ARM", "params": {"force": 1}})
            assert response.status_code == 200, response.text
            waiting = response.json()
            assert waiting["status"] == "WAITING_FOR_PI"
            assert waiting["physical_command_dispatch"] is False
            assert waiting["pi_transport_confirmed"] is False
            command = pi.receive_json()
            assert set(command) == {
                "type", "vehicle_id", "request_id", "control_epoch", "challenge",
            }
            assert command["type"] == "control_transport_probe"
            assert command["control_epoch"] == 1
            assert command["request_id"] == waiting["request_id"]
            assert "action" not in command and "mavlink" not in command
            receipt_path = path + "/" + waiting["request_id"]
            assert client.get(receipt_path).status_code == 403
            assert client.get(receipt_path, headers=pilot_headers).json()["status"] == "WAITING_FOR_PI"
            assert client.post(path, headers=pilot_headers).status_code == 409

            ack = pi_probe_reply(command, VEHICLE)
            assert ack is not None
            pi.send_text(json.dumps({**ack, "challenge": "0" * 32}))
            pi.send_text(json.dumps({**ack, "uart_touched": True}))
            for _ in range(10):
                assert client.get(receipt_path, headers=pilot_headers).json()["status"] == "WAITING_FOR_PI"
                time.sleep(0.005)
            pi.send_text(json.dumps(ack))
            for _ in range(100):
                final = client.get(receipt_path, headers=pilot_headers).json()
                if final["status"] == "PI_ECHO_NO_ACTUATION":
                    break
                time.sleep(0.01)
            assert final["status"] == "PI_ECHO_NO_ACTUATION"
            assert final["pi_transport_confirmed"] is True
            assert final["fc_ack_received"] is False
            assert final["physical_command_dispatch"] is False
            assert final["flight_control_enabled"] is False
            pi.send_text(json.dumps(ack))  # duplicate cannot create a second completion
            assert client.get(receipt_path, headers=pilot_headers).json() == final
        # A completed probe must not survive as 'current physical authority'.
        assert client.get("/api/v1/flight-control/status").json()["flight_control_enabled"] is False


def test_device_disconnect_invalidates_outstanding_shadow_probe():
    relay_cfg, control_cfg, device_token = make_configs()
    path = f"/api/v1/vehicles/{VEHICLE}/control/transport/probe"
    with TestClient(create_app(relay_cfg, control_cfg)) as client:
        pilot, _ = authenticated_roles(client)
        h = {"Authorization": "Bearer " + pilot}
        with client.websocket_connect(
            f"/ws/mavlink/device/{VEHICLE}",
            headers={"Authorization": "Bearer " + device_token},
        ) as pi:
            response = client.post(path, headers=h).json()
            stale_request = pi.receive_json()
        receipt_path = path + "/" + response["request_id"]
        # Wait for the server's websocket disconnect cleanup.
        for _ in range(100):
            status = client.get(receipt_path, headers=h).json()["status"]
            if status == "CANCELLED_DEVICE_SESSION":
                break
            time.sleep(0.01)
        assert status == "CANCELLED_DEVICE_SESSION"
        with client.websocket_connect(
            f"/ws/mavlink/device/{VEHICLE}",
            headers={"Authorization": "Bearer " + device_token},
        ) as pi:
            pi.send_text(json.dumps(pi_probe_reply(stale_request, VEHICLE)))
            assert client.get(receipt_path, headers=h).json()["status"] == "CANCELLED_DEVICE_SESSION"
            renewed = client.post(path, headers=h)
            assert renewed.status_code == 200
            next_request = pi.receive_json()
            assert next_request["control_epoch"] == stale_request["control_epoch"] + 1
            assert next_request["request_id"] != stale_request["request_id"]


def test_shadow_probe_expiry_epoch_mismatch_and_rate_bounds():
    tick = [10.0]
    spy = AsyncMock()
    state = SimpleNamespace(
        connected=True, device_socket=SimpleNamespace(send_json=spy),
        lock=asyncio.Lock(), control_epoch=9,
    )
    shadow = ControlTransportProbe(clock=lambda: tick[0])

    async def scenario():
        first = await shadow.issue(state, VEHICLE)
        request = spy.call_args.args[0]
        wrong_epoch_ack = pi_probe_reply(request, VEHICLE)
        assert wrong_epoch_ack is not None
        assert not shadow.observe_ack(json.dumps(wrong_epoch_ack), epoch=10)
        assert shadow.receipt(first["request_id"], 10)["status"] == "CANCELLED_EPOCH_CHANGED"
        tick[0] += 2.5
        next_probe = await shadow.issue(state, VEHICLE)
        assert next_probe["status"] == "WAITING_FOR_PI"
        tick[0] += 4.5
        assert shadow.receipt(next_probe["request_id"], 9)["status"] == "EXPIRED_NO_REPLAY"
        assert not shadow.observe_ack(json.dumps(pi_probe_reply(spy.call_args.args[0], VEHICLE)), epoch=9)
        third = await shadow.issue(state, VEHICLE)
        assert third["status"] == "WAITING_FOR_PI"
        tick[0] += 5
        shadow.receipt(third["request_id"], 9)
        try:
            await shadow.issue(state, VEHICLE)
        except Exception as exc:
            assert "rate limit" in str(exc)
        else:
            raise AssertionError("Session quota bypassed")
        assert spy.await_count == 3

    asyncio.run(scenario())


def test_default_disabled_control_api_cannot_become_shadow_authority():
    relay_cfg, _, device_token = make_configs()
    path = f"/api/v1/vehicles/{VEHICLE}/control/transport/probe"
    with TestClient(create_app(relay_cfg)) as client:
        with client.websocket_connect(
            f"/ws/mavlink/device/{VEHICLE}",
            headers={"Authorization": "Bearer " + device_token},
        ):
            assert client.post(path).status_code == 403
            s = client.get("/api/v1/flight-control/status").json()
            assert s["pilot_to_pi_shadow_probe"] is False
            assert s["native_mavlink_command_tunnel"] is False
            assert s["flight_control_enabled"] is False
