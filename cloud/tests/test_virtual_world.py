"""Two virtual UAVs receive jobs and publish trajectory samples (not SITL)."""
from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from cloud.app.main import create_app
from cloud.app.planner import VehicleProfile
from cloud.tools.virtual_world import VirtualRouteWorker
from cloud.tests.test_control_plane import (
    acquire, approve, config, draft, jwt_header, register, start,
)


def virtual_token(name):
    return jwt_header("virtual-worker-" + name, kind="device",
                      device=name, scopes="jobs:claim jobs:ack telemetry:write")[
                          "Authorization"][7:]


def test_cloud_schedules_two_virtual_vehicles_and_observes_route(monkeypatch):
    monkeypatch.setenv("AEROHELM_VIRTUAL_DISPATCH_ENABLED", "true")
    with TestClient(create_app(config())) as client:
        homes = {"SIM-A":12.97, "SIM-B":12.99}
        for name, home_lat in homes.items():
            assert register(client, name, home_lat).status_code == 201
        mission = draft(client).json()
        assert approve(client, mission).status_code == 200
        for name in homes:
            assert acquire(client, name).status_code == 200
        assert start(client, mission).status_code == 202
        for name, latitude in homes.items():
            worker = VirtualRouteWorker(
                client, device_id=name, bearer=virtual_token(name),
                home=VehicleProfile(
                    home_lat=latitude, home_lon=77.59, energy_wh=180,
                    reserve_fraction=.3, energy_wh_per_km=25),
                outbound_steps=5,
            )
            done = worker.execute_one()
            assert done["status"] == "COMPLETED_SIM"
            assert done["telemetry_samples"] == 10
            assert done["physical_flight_performed"] is False
            telemetry = client.get("/api/v1/vehicles/" + name + "/telemetry",
                                   headers=jwt_header()).json()
            assert telemetry["fresh"] is True
            assert telemetry["last_sequence"] == 10
            assert telemetry["observation"]["mode"] == "VIRTUAL_ROUTE"
            assert telemetry["observation"]["armed"] is False
            assert telemetry["flight_ready"] is False
            assert telemetry["observation_source"] == "VIRTUAL_SIMULATED"
            assert worker.execute_one()["status"] == "IDLE_SIM"
        final = client.get("/api/v1/missions/" + mission["id"],
                           headers=jwt_header()).json()
        assert final["status"] == "COMPLETED_SIM"
        assert final["physical_flight_authorized"] is False


def test_virtual_worker_refuses_real_identity_even_with_valid_bearer():
    with pytest.raises(ValueError):
        VirtualRouteWorker(
            None, device_id="PIXHAWK-REAL", bearer="fake-token",
            home=VehicleProfile(home_lat=12.97, home_lon=77.59,
                                energy_wh=100, reserve_fraction=.3,
                                energy_wh_per_km=20))


def test_virtual_route_network_failure_never_fabricates_completion(monkeypatch):
    import httpx
    from sqlalchemy.orm import Session
    from sqlalchemy import select
    from cloud.app.models import Job
    import time

    monkeypatch.setenv("AEROHELM_VIRTUAL_DISPATCH_ENABLED", "true")
    with TestClient(create_app(config())) as client:
        register(client, "SIM-A", 12.97)
        plan = {"request_id":"d"*32, "vehicle_ids":["SIM-A"], "tasks":[{
            "key":"TASK-ONE","latitude":12.9701,"longitude":77.5901,
            "relative_altitude_m":30,"required_energy_wh":3,"priority":5,
        }]}
        mission = draft(client, plan=plan).json()
        approve(client, mission)
        acquire(client, "SIM-A")
        start(client, mission)

        class UnreliableClient:
            def __init__(self, inner):
                self.inner = inner
                self.sent = 0

            def post(self, path, **kwargs):
                if path == "/api/v1/device/telemetry":
                    self.sent += 1
                    if self.sent == 2:
                        return httpx.Response(
                            503, request=httpx.Request("POST", "https://offline.invalid/"),
                            json={"detail":"network interrupted"},
                        )
                return self.inner.post(path, **kwargs)

        unreliable = UnreliableClient(client)
        worker = VirtualRouteWorker(
            unreliable, device_id="SIM-A", bearer=virtual_token("SIM-A"),
            home=VehicleProfile(home_lat=12.97, home_lon=77.59,
                                energy_wh=180, reserve_fraction=.3,
                                energy_wh_per_km=25),
        )
        with pytest.raises(httpx.HTTPStatusError):
            worker.execute_one()
        assert unreliable.sent == 2
        current = client.get("/api/v1/missions/" + mission["id"],
                             headers=jwt_header()).json()
        assert current["status"] == "RUNNING_SIM"
        assert current["jobs"][0]["status"] == "ACCEPTED_SIM"
        with Session(client.app.state.engine) as session:
            with session.begin():
                row = session.get(Job, current["jobs"][0]["id"])
                row.deadline = time.time()-1
        client.post("/api/v1/internal/sweep", headers=jwt_header())
        terminal = client.get("/api/v1/missions/" + mission["id"],
                              headers=jwt_header()).json()
        assert terminal["status"] == "FAILED_SIM"
        assert terminal["jobs"][0]["status"] == "TIMED_OUT"
