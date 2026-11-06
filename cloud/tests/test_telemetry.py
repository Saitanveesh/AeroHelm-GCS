"""Real HTTP telemetry identity/epoch boundaries without physical aircraft."""
from __future__ import annotations

import time

from fastapi.testclient import TestClient
from sqlalchemy.orm import Session
from sqlalchemy import select

from cloud.app.main import create_app
from cloud.app.models import TelemetryLatest
from cloud.tests.test_control_plane import config, jwt_header, register


def dev(name, tenant="TENANT-A"):
    return jwt_header("pi-" + name, tenant=tenant, kind="device",
                      device=name, scopes="telemetry:write")


def sample(epoch, sequence):
    return {
        "epoch": epoch, "sequence": sequence, "latitude": 12.9701,
        "longitude": 77.5901, "altitude_m": 31.2,
        "battery_pct": 78.0, "armed": False, "mode": "STANDBY",
        "link_rtt_ms": 100.5, "gps_fix_type": 3,
    }


def test_real_hardware_is_observable_but_never_actuated():
    with TestClient(create_app(config())) as client:
        assert register(client, "PIX-01", kind="hardware").status_code == 201
        assert client.post("/api/v1/device/telemetry",
                           headers=dev("PIX-01"),
                           json=sample(1, 1)).status_code == 409
        opened = client.post("/api/v1/device/telemetry/session",
                             headers=dev("PIX-01"))
        assert opened.status_code == 200
        epoch = opened.json()["session_epoch"]
        assert epoch == 1
        assert opened.json()["physical_control_authorized"] is False
        response = client.post("/api/v1/device/telemetry",
                               headers=dev("PIX-01"),
                               json=sample(epoch, 1))
        assert response.status_code == 202
        state = client.get("/api/v1/vehicles/PIX-01/telemetry",
                           headers=jwt_header()).json()
        assert state["fresh"] is True
        assert state["observation"]["battery_pct"] == 78
        assert state["flight_ready"] is False
        assert state["observation_source"] == "DEVICE_REPORTED_UNVERIFIED"
        assert client.get("/api/v1/fleet/status", headers=jwt_header()).json()[
            "cloud_flight_authorized"] is False
        assert client.post("/api/v1/vehicles/PIX-01/lease",
                           headers=jwt_header(),
                           json={"duration_seconds":30}).status_code == 403


def test_strict_sequence_session_fencing_and_device_identity():
    with TestClient(create_app(config())) as client:
        register(client, "SIM-A")
        register(client, "SIM-B")
        opened = client.post("/api/v1/device/telemetry/session",
                             headers=dev("SIM-A")).json()["session_epoch"]
        assert client.post("/api/v1/device/telemetry", headers=dev("SIM-B"),
                           json=sample(opened, 1)).status_code == 409
        assert client.post("/api/v1/device/telemetry", headers=dev("SIM-A"),
                           json=sample(opened, 1)).status_code == 202
        assert client.post("/api/v1/device/telemetry", headers=dev("SIM-A"),
                           json=sample(opened, 1)).status_code == 409
        assert client.post("/api/v1/device/telemetry", headers=dev("SIM-A"),
                           json=sample(opened, 0)).status_code == 422
        assert client.post("/api/v1/device/telemetry", headers=dev("SIM-A"),
                           json=sample(opened, 2)).status_code == 202
        restarted = client.post("/api/v1/device/telemetry/session",
                                headers=dev("SIM-A")).json()
        assert restarted["session_epoch"] == opened + 1
        stale = client.get("/api/v1/vehicles/SIM-A/telemetry",
                           headers=jwt_header()).json()
        assert stale["fresh"] is False and stale["observation"] is None
        assert client.post("/api/v1/device/telemetry", headers=dev("SIM-A"),
                           json=sample(opened, 3)).status_code == 409
        assert client.post("/api/v1/device/telemetry", headers=dev("SIM-A"),
                           json=sample(restarted["session_epoch"], 1)).status_code == 202


def test_stale_telemetry_cannot_be_mistaken_for_live_flight_authority():
    with TestClient(create_app(config())) as client:
        register(client, "SIM-A")
        opened = client.post("/api/v1/device/telemetry/session",
                             headers=dev("SIM-A")).json()["session_epoch"]
        client.post("/api/v1/device/telemetry", headers=dev("SIM-A"),
                    json=sample(opened, 1))
        with Session(client.app.state.engine) as session:
            with session.begin():
                row = session.scalar(select(TelemetryLatest))
                row.received_at = time.time() - 30
        state = client.get("/api/v1/vehicles/SIM-A/telemetry",
                           headers=jwt_header()).json()
        assert state["fresh"] is False
        assert state["age_s"] >= 25
        assert state["flight_ready"] is False


def test_telemetry_rejects_mavlink_or_command_payloads():
    with TestClient(create_app(config())) as client:
        register(client, "SIM-A")
        opened = client.post("/api/v1/device/telemetry/session",
                             headers=dev("SIM-A")).json()["session_epoch"]
        forged = {**sample(opened, 1), "mavlink_command": "ARM"}
        assert client.post("/api/v1/device/telemetry", headers=dev("SIM-A"),
                           json=forged).status_code == 422
        assert client.post("/api/v1/device/telemetry", headers=jwt_header(),
                           json=sample(opened, 1)).status_code == 403


def test_cross_tenant_observation_never_leaks():
    with TestClient(create_app(config())) as client:
        register(client, "SIM-A")
        assert client.get("/api/v1/vehicles/SIM-A/telemetry",
                          headers=jwt_header(tenant="TENANT-B")).status_code == 404
        assert client.post("/api/v1/device/telemetry/session",
                           headers=dev("SIM-A", tenant="TENANT-B")).status_code == 404


def test_reported_battery_and_gnss_risks_are_advisory_only():
    with TestClient(create_app(config())) as client:
        assert register(client, "PIX-01", kind="hardware").status_code == 201
        epoch = client.post("/api/v1/device/telemetry/session",
                            headers=dev("PIX-01")).json()["session_epoch"]
        reported = {**sample(epoch, 1), "battery_pct": 12.0,
                    "gps_fix_type": 1}
        assert client.post("/api/v1/device/telemetry", headers=dev("PIX-01"),
                           json=reported).status_code == 202
        state = client.get("/api/v1/vehicles/PIX-01/telemetry",
                           headers=jwt_header()).json()
        assert set(state["advisory_risks"]) == {
            "REPORTED_BATTERY_LOW", "REPORTED_GNSS_DEGRADED",
        }
        assert state["flight_ready"] is False
        assert client.post("/api/v1/vehicles/PIX-01/lease",
                           headers=jwt_header(), json={
                               "duration_seconds":30}).status_code == 403
