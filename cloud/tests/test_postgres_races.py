"""PostgreSQL-specific race/fencing test. Skipped in SQLite-only CI job."""
from __future__ import annotations

import os
import uuid
from concurrent.futures import ThreadPoolExecutor

import pytest
from fastapi.testclient import TestClient

from cloud.app.main import create_app
from cloud.tests.test_control_plane import config, jwt_header

POSTGRES = os.environ.get("AEROHELM_DATABASE_URL", "")
pytestmark = pytest.mark.skipif(
    not POSTGRES.startswith("postgresql+psycopg://"),
    reason="Requires CI PostgreSQL service and migrated schema",
)


def test_one_virtual_lease_holder_and_one_active_mission_across_racing_calls(monkeypatch):
    monkeypatch.setenv("AEROHELM_VIRTUAL_DISPATCH_ENABLED", "true")
    tenant = "TENANT-RACE-" + uuid.uuid4().hex[:12]
    def headers(actor):
        return jwt_header(actor, tenant=tenant)
    # Two distinct ASGI apps and independent SQLAlchemy engines share the
    # same PostgreSQL transaction authority; in-memory locks cannot help.
    with TestClient(create_app(config(POSTGRES))) as client, \
         TestClient(create_app(config(POSTGRES))) as replica:
        register = client.post("/api/v1/vehicles", headers=headers("admin"), json={
            "vehicle_id":"SIM-RACE","kind":"virtual","profile":{
                "home_lat":12.97,"home_lon":77.59,"energy_wh":150,
                "reserve_fraction":.30,"energy_wh_per_km":20,
            },
        })
        assert register.status_code == 201, register.text

        def lease(pair):
            actor, endpoint = pair
            return endpoint.post("/api/v1/vehicles/SIM-RACE/lease",
                                 headers=headers(actor),
                                 json={"duration_seconds":60})
        with ThreadPoolExecutor(max_workers=2) as pool:
            attempts = list(pool.map(lease, [
                ("runner", client), ("rival", replica),
            ]))
        assert sorted([x.status_code for x in attempts]) == [200, 409]
        holder = "runner" if attempts[0].status_code == 200 else "rival"

        records = []
        for index in range(2):
            plan = {"request_id":uuid.uuid4().hex,
                    "vehicle_ids":["SIM-RACE"],"tasks":[{
                        "key":"TASK-0"+str(index),"latitude":12.9701,
                        "longitude":77.5901,"relative_altitude_m":30,
                        "required_energy_wh":3,"priority":5,
                    }]}
            mission = client.post("/api/v1/missions", headers=headers("author"),
                                  json=plan)
            assert mission.status_code == 201, mission.text
            body = mission.json()
            approved = client.post(
                "/api/v1/missions/" + body["id"] + "/approve",
                headers=headers("reviewer"), json={
                    "request_id":uuid.uuid4().hex,
                    "plan_sha256":body["plan_sha256"],
                })
            assert approved.status_code == 200, approved.text
            records.append(body["id"])

        def start(args):
            ident, endpoint = args
            return endpoint.post("/api/v1/missions/"+ident+"/start",
                                 headers=headers(holder),
                                 json={"request_id":uuid.uuid4().hex})
        with ThreadPoolExecutor(max_workers=2) as pool:
            attempts = list(pool.map(start, [
                (records[0], client), (records[1], replica),
            ]))
        assert sorted([x.status_code for x in attempts]) == [202, 409]
        assert sum(client.get("/api/v1/missions/"+ident,
                              headers=headers("admin")).json()["status"] == "RUNNING_SIM"
                   for ident in records) == 1
