"""No-hardware flight authority API regression tests."""
from __future__ import annotations

import hashlib
import time

from argon2 import PasswordHasher
from fastapi.testclient import TestClient
import pytest
from pymavlink import mavutil

from services.vihanga_link.control_api import ControlSettings
from services.vihanga_link.control_policy import ObservedVehicle
from services.vihanga_link.relay import Config, create_app


VEHICLE = "AH-HW-001"
PLAN = "f" * 64


def make_configs():
    ph = PasswordHasher()
    token = "local-only-device-token-" + "e"*48
    relay_cfg = Config(
        vehicle_id=VEHICLE, operator_name="telemetry",
        operator_password_hash=ph.hash("telemetry-test-pass"),
        jwt_key="T" * 48,
        device_token_sha256=hashlib.sha256(token.encode()).hexdigest(),
        allowed_hosts=("testserver",),
    )
    control_cfg = ControlSettings(
        enabled=True, pilot_name="pilot1",
        pilot_password_hash=ph.hash("pilot-controlled-password"),
        approver_name="safety1",
        approver_password_hash=ph.hash("distinct-safety-password"),
        jwt_key="Q" * 48,
    )
    return relay_cfg, control_cfg, token


def authenticated_roles(client):
    def login(user, password, role):
        reply = client.post("/api/v1/flight-control/auth/login", json={
            "username": user, "password": password, "role": role,
        })
        assert reply.status_code == 200, reply.text
        return reply.json()["access_token"]
    return (
        login("pilot1", "pilot-controlled-password", "pilot"),
        login("safety1", "distinct-safety-password", "approver"),
    )


def physical_state():
    return ObservedVehicle(
        vehicle_id=VEHICLE, system_id=1, source="PHYSICAL_FC",
        heartbeat_age_ms=100, attitude_age_ms=100, gps_age_ms=100,
        gps_fix=3, home_verified=True, battery_percent=80,
        ekf_healthy=True, geofence_healthy=True, rc_link_healthy=True,
        rc_override_active=False, on_ground=True, armed=False,
        mode="GUIDED", failsafe_clear=True,
    )


def heartbeat():
    codec = mavutil.mavlink.MAVLink(None, srcSystem=1, srcComponent=1)
    return mavutil.mavlink.MAVLink_heartbeat_message(
        type=mavutil.mavlink.MAV_TYPE_QUADROTOR,
        autopilot=mavutil.mavlink.MAV_AUTOPILOT_ARDUPILOTMEGA,
        base_mode=0, custom_mode=0,
        system_status=mavutil.mavlink.MAV_STATE_STANDBY,
        mavlink_version=3,
    ).pack(codec)


def wait_heartbeat(client):
    for _ in range(100):
        if client.get("/api/v1/mavlink/health").json()["native_hud_heartbeat_ready"]:
            return
        time.sleep(0.01)
    raise AssertionError("Synthetic offline FC heartbeat not observed")


def test_flight_control_disabled_by_default_without_existing_telemetry_login_changes():
    relay_cfg, _, _ = make_configs()
    with TestClient(create_app(relay_cfg)) as client:
        state = client.get("/api/v1/flight-control/status").json()
        assert state["mode"] == "DISABLED"
        assert state["flight_control_enabled"] is False
        assert state["physical_command_dispatch"] is False
        assert client.post("/api/v1/flight-control/auth/login", json={
            "username": "pilot1", "password": "pilot-controlled-password",
            "role": "pilot",
        }).status_code == 403
        login = client.post("/api/v1/auth/login", json={
            "username": "telemetry", "password": "telemetry-test-pass",
        })
        assert login.status_code == 200


def test_independent_roles_lease_policy_and_receipts_never_contact_fc():
    relay_cfg, control_cfg, device_token = make_configs()
    with TestClient(create_app(relay_cfg, control_cfg)) as client:
        status = client.get("/api/v1/flight-control/status").json()
        assert status["mode"] == "SIMULATOR_AUTHORITY_ONLY"
        assert status["physical_command_dispatch"] is False
        pilot, approver = authenticated_roles(client)
        path = "/api/v1/vehicles/" + VEHICLE + "/control"
        pilot_headers = {"Authorization": "Bearer " + pilot}
        approval_headers = {**pilot_headers,
                            "X-Approver-Authorization": "Bearer " + approver}
        body = {"plan_sha256": PLAN}
        assert client.post(path + "/lease", json=body).status_code == 403
        assert client.post(path + "/lease", headers=pilot_headers,
                           json=body).status_code == 403
        assert client.post(path + "/lease", headers=approval_headers,
                           json=body).status_code == 409  # no FC
        with client.websocket_connect(
                "/ws/mavlink/device/" + VEHICLE,
                headers={"Authorization": "Bearer " + device_token}) as device:
            device.send_bytes(heartbeat())
            wait_heartbeat(client)
            lease_request = client.post(path + "/lease",
                                        headers=approval_headers, json=body)
            assert lease_request.status_code == 200, lease_request.text
            lease = lease_request.json()
            assert lease["control_epoch"] == 1
            assert client.post(path + "/lease", headers=approval_headers,
                               json=body).status_code == 409
            command = {
                "request_id": "e"*32, "sequence": 1, "action": "ARM",
                "plan_sha256": PLAN, "params": {},
            }
            command_headers = {**pilot_headers,
                               "X-Control-Lease": lease["lease_token"]}
            # Real relay evidence does NOT certify RC/ EKF/ battery/ fences.
            assert client.post(path + "/commands", headers=command_headers,
                               json=command).status_code == 409
            # Injection is offline-test-only: still NEVER transmits to Pi.
            client.app.state.control.evidence_provider = physical_state
            response = client.post(path + "/commands",
                                   headers=command_headers, json=command)
            assert response.status_code == 200, response.text
            assert response.json()["status"] == "AUTHORIZED_NOT_DISPATCHED"
            assert response.json()["flight_executed"] is False
            assert client.get(path + "/commands/" + command["request_id"],
                              headers=pilot_headers).json()["pi_received"] is False
            # Since no command is sent to the Pi, the only real WSS path
            # remains telemetry and restricted read-only diagnostics.
        assert client.post(path + "/commands", headers=command_headers,
                           json={**command, "sequence": 2}).status_code == 409
        assert client.post(path + "/revoke", headers=pilot_headers).json()["status"] == "REVOKED"


def test_control_credential_separation_and_denied_roles():
    relay_cfg, control_cfg, _ = make_configs()
    with TestClient(create_app(relay_cfg, control_cfg)) as client:
        assert client.post("/api/v1/flight-control/auth/login", json={
            "username": "pilot1", "password": "distinct-safety-password",
            "role": "pilot",
        }).status_code == 401
        assert client.post("/api/v1/flight-control/auth/login", json={
            "username": "safety1", "password": "pilot-controlled-password",
            "role": "approver",
        }).status_code == 401


def test_control_config_rejects_same_identity_or_missing_secrets():
    with pytest.raises(ValueError):
        ControlSettings(enabled=True, pilot_name="same", approver_name="same",
                        pilot_password_hash="$argon2id$stub",
                        approver_password_hash="$argon2id$stub",
                        jwt_key="x"*48).validate()
    with pytest.raises(ValueError):
        ControlSettings(enabled=True, pilot_name="pilot1", approver_name="safety1",
                        pilot_password_hash="", approver_password_hash="$argon2id$stub",
                        jwt_key="x"*48).validate()
