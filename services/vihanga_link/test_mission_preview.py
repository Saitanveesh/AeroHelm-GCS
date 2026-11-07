"""Offline-only autonomy preview tests. No serial or command channel involved."""
from __future__ import annotations

import hashlib
from argon2 import PasswordHasher
from fastapi.testclient import TestClient
import pytest
from pydantic import ValidationError

from services.vihanga_link.mission_preview import (
    PreviewRequest, haversine_m, preview
)
from services.vihanga_link.relay import Config, create_app


def fixture_request() -> dict:
    return {
        "vehicle_id": "AH-HW-001",
        "bounds": {
            "min_lat": 12.0, "max_lat": 13.0,
            "min_lon": 77.0, "max_lon": 78.0,
            "min_altitude_m": 5.0, "max_altitude_m": 110.0
        },
        "route": [
            {"lat": 12.90, "lon": 77.60, "altitude_m": 40},
            {"lat": 12.901, "lon": 77.60, "altitude_m": 40},
            {"lat": 12.901, "lon": 77.601, "altitude_m": 45}
        ],
        "speed_mps": 5.0,
        "estimated_max_flight_seconds": 1000,
        "reserve_percent": 25.0,
        "camera_interval_seconds": 2.5
    }


def test_geometry_digest_and_replay_stability():
    mission = PreviewRequest.model_validate(fixture_request())
    a = preview(mission)
    b = preview(PreviewRequest.model_validate(fixture_request()))
    assert a == b
    assert len(a["revision_sha256"]) == 64
    assert a["horizontal_distance_m"] > 100
    assert a["estimated_seconds"] > 0
    assert a["camera_capture_estimate"] > 1
    assert a["status"] == "SIMULATION_PREVIEW_ONLY"
    assert a["vehicle_command_authorized"] is False
    assert a["timeline"][0]["eta_seconds"] == 0
    assert a["timeline"][-1]["eta_seconds"] == a["estimated_seconds"]
    changed = fixture_request()
    changed["route"][2]["altitude_m"] = 46
    assert preview(PreviewRequest.model_validate(changed))["revision_sha256"] != a["revision_sha256"]


def test_reject_geofence_altitude_and_invalid_numeric_values():
    cases = [
        lambda x: x["route"][0].update(lat=11.99),
        lambda x: x["route"][0].update(altitude_m=150.0),
        lambda x: x["route"][0].update(lon=float("nan")),
        lambda x: x["bounds"].update(max_lat=11.9),
        lambda x: x["bounds"].update(min_altitude_m=99.0),
        lambda x: x["route"].append({"lat": 13.10, "lon": 77.6, "altitude_m": 40})
    ]
    for mutate in cases:
        mission = fixture_request()
        mutate(mission)
        with pytest.raises(ValidationError):
            PreviewRequest.model_validate(mission)


def test_reject_too_long_and_exhausted_reserve():
    request = fixture_request()
    request["estimated_max_flight_seconds"] = 60
    request["reserve_percent"] = 70
    with pytest.raises(ValueError, match="reserve-adjusted"):
        preview(PreviewRequest.model_validate(request))
    request = fixture_request()
    request["route"][1] = request["route"][0].copy()
    with pytest.raises(ValueError, match="distinct"):
        preview(PreviewRequest.model_validate(request))


def test_no_auth_no_preview_and_no_control_endpoint():
    cfg = Config(
        vehicle_id="AH-HW-001",
        operator_name="operator",
        operator_password_hash=PasswordHasher().hash("unique-test-only-password"),
        jwt_key="Z" * 48,
        device_token_sha256=hashlib.sha256(b"fake-device-token").hexdigest(),
        allowed_hosts=("testserver",)
    )
    app = create_app(cfg)
    with TestClient(app) as client:
        mission = fixture_request()
        assert client.post("/api/v1/sim/mission/preview", json=mission).status_code == 401
        signin = client.post("/api/v1/auth/login", json={
            "username": "operator", "password": "unique-test-only-password"
        })
        assert signin.status_code == 200
        headers = {"Authorization": "Bearer " + signin.json()["access_token"]}
        response = client.post("/api/v1/sim/mission/preview",
                               json=mission, headers=headers)
        assert response.status_code == 200
        assert response.json()["vehicle_command_authorized"] is False
        wrong_vehicle = fixture_request()
        wrong_vehicle["vehicle_id"] = "OTHER-UAV"
        assert client.post("/api/v1/sim/mission/preview", json=wrong_vehicle,
                           headers=headers).status_code == 404
        assert client.post("/api/v1/vehicles/AH-HW-001/arm",
                           headers=headers).status_code in (404, 405)
        assert client.post("/api/v1/mission/execute",
                           headers=headers).status_code in (404, 405)
