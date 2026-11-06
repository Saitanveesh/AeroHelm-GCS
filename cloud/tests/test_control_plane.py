"""Hardware-free end-to-end Cloud Fleet Control Plane API regression tests."""
from __future__ import annotations

import time
import uuid

import jwt
import pytest
from fastapi.testclient import TestClient
from sqlalchemy import select
from sqlalchemy.orm import Session

from cloud.app.config import Settings
from cloud.app.main import create_app
from cloud.app.models import Job, Lease, Mission

TEST_SECRET = "only-tests-not-production-32-character-secret-v1"
SCOPES = ("fleet:admin fleet:read fleet:operate mission:write mission:approve "
          "mission:execute jobs:claim jobs:ack audit:read scheduler:tick")


def config(db="sqlite+pysqlite:///:memory:"):
    return Settings("test", db, "aerohelm-cloud-test", "aerohelm-cloud", None, TEST_SECRET)


def jwt_header(actor="operator-a", *, tenant="TENANT-A", kind="operator",
               device=None, scopes=SCOPES):
    t = int(time.time())
    payload = {"iss": "aerohelm-cloud-test", "aud": "aerohelm-cloud",
               "sub": actor, "tenant": tenant, "scope": scopes, "kind": kind,
               "iat": t, "nbf": t, "exp": t+300}
    if device:
        payload["vehicle_id"] = device
    return {"Authorization": "Bearer " + jwt.encode(payload, TEST_SECRET, algorithm="HS256")}


@pytest.fixture
def client(monkeypatch):
    monkeypatch.setenv("AEROHELM_VIRTUAL_DISPATCH_ENABLED", "true")
    app = create_app(config())
    with TestClient(app) as c:
        yield c


def profile(lat, lon=77.59):
    return {"home_lat": lat, "home_lon": lon,
            "energy_wh": 180.0, "reserve_fraction": .3, "energy_wh_per_km": 25.0}


def register(client, name, lat=12.97, *, kind="virtual", actor="admin", tenant="TENANT-A"):
    return client.post("/api/v1/vehicles", headers=jwt_header(actor, tenant=tenant),
                       json={"vehicle_id": name, "kind": kind, "profile": profile(lat)})


def spec(ids=("SIM-A", "SIM-B"), *, request_id=None):
    return {"request_id": request_id or uuid.uuid4().hex,
            "vehicle_ids": list(ids), "tasks": [
                {"key": "TASK-A", "latitude": 12.9701, "longitude": 77.5901,
                 "relative_altitude_m": 30, "required_energy_wh": 5, "priority": 8},
                {"key": "TASK-B", "latitude": 12.9901, "longitude": 77.5901,
                 "relative_altitude_m": 30, "required_energy_wh": 5, "priority": 4},
            ]}


def make_two(client):
    assert register(client, "SIM-A", 12.97).status_code == 201
    assert register(client, "SIM-B", 12.99).status_code == 201


def draft(client, *, plan=None, actor="author"):
    return client.post("/api/v1/missions", headers=jwt_header(actor), json=plan or spec())


def approve(client, mission, *, actor="approver", request_id=None, digest=None):
    return client.post("/api/v1/missions/" + mission["id"] + "/approve",
                       headers=jwt_header(actor),
                       json={"request_id": request_id or uuid.uuid4().hex,
                             "plan_sha256": digest or mission["plan_sha256"]})


def acquire(client, ident, actor="runner"):
    return client.post("/api/v1/vehicles/" + ident + "/lease",
                       headers=jwt_header(actor), json={"duration_seconds": 60})


def start(client, mission, actor="runner", request_id=None):
    return client.post("/api/v1/missions/" + mission["id"] + "/start",
                       headers=jwt_header(actor),
                       json={"request_id": request_id or uuid.uuid4().hex})


def device(client, ident):
    return jwt_header("device-" + ident, kind="device", device=ident,
                      scopes="jobs:claim jobs:ack")


def claim(client, ident):
    return client.post("/api/v1/device/jobs/claim", headers=device(client, ident),
                       json={})


def ack(client, ident, job, phase, event_id=None, token=None):
    return client.post("/api/v1/device/jobs/" + job["id"] + "/ack",
                       headers=device(client, ident), json={
                           "event_id": event_id or uuid.uuid4().hex,
                           "claim_token": token or job["claim_token"], "phase": phase})


def setup_started(client):
    make_two(client)
    mission = draft(client).json()
    assert approve(client, mission).status_code == 200
    for name in ("SIM-A", "SIM-B"):
        assert acquire(client, name).status_code == 200
    started = start(client, mission)
    assert started.status_code == 202, started.text
    return mission


def test_registry_scopes_and_tenant_boundaries(client):
    assert client.get("/api/v1/vehicles").status_code == 401
    assert register(client, "SIM-A").status_code == 201
    assert register(client, "SIM-A").status_code == 409
    assert register(client, "SIM-A", tenant="TENANT-B").status_code == 201
    assert len(client.get("/api/v1/vehicles", headers=jwt_header()).json()["vehicles"]) == 1
    assert len(client.get("/api/v1/vehicles",
                          headers=jwt_header(tenant="TENANT-B")).json()["vehicles"]) == 1
    assert client.post("/api/v1/vehicles",
                       headers=jwt_header(scopes="fleet:read"),
                       json={"vehicle_id":"SIM-C","kind":"virtual",
                             "profile":profile(12.97)}).status_code == 403
    assert client.get("/api/v1/capabilities",
                      headers=jwt_header()).json()["physical_aircraft_dispatch"] is False


def test_hardware_never_eligible_for_dispatch_or_authority(client):
    assert register(client, "PX4-REAL", kind="hardware").status_code == 201
    assert client.post("/api/v1/vehicles/PX4-REAL/lease",
                       headers=jwt_header(), json={"duration_seconds":60}).status_code == 403
    attempted = spec(("PX4-REAL",))
    attempted["tasks"] = attempted["tasks"][:1]
    assert draft(client, plan=attempted).status_code == 403
    assert claim(client, "PX4-REAL").json()["job"] is None
    assert client.post("/api/v1/missions", headers=device(client, "PX4-REAL"),
                       json=attempted).status_code == 403


def test_draft_requires_digest_independent_approval_and_valid_lease(client):
    make_two(client)
    source = spec()
    first = draft(client, plan=source)
    assert first.status_code == 201, first.text
    mission = first.json()
    assert mission["execution_target"] == "VIRTUAL_SIMULATOR_ONLY"
    assert len(mission["jobs"]) == 2
    assert draft(client, plan=source).json()["id"] == mission["id"]
    alternate = spec(request_id=source["request_id"])
    alternate["tasks"][0]["required_energy_wh"] = 7
    assert draft(client, plan=alternate).status_code == 409
    assert approve(client, mission, actor="author").status_code == 403
    assert approve(client, mission, digest="0" * 64).status_code == 409
    good = approve(client, mission)
    assert good.status_code == 200
    assert good.json()["approved_by"] == "approver"
    assert start(client, mission).status_code == 409
    assert acquire(client, "SIM-A").status_code == 200
    assert start(client, mission).status_code == 409
    assert acquire(client, "SIM-B").status_code == 200
    key = uuid.uuid4().hex
    assert start(client, mission, request_id=key).status_code == 202
    assert start(client, mission, request_id=key).status_code == 202
    assert start(client, mission).status_code == 409


def test_two_virtual_uavs_receive_only_their_jobs_and_complete(client):
    mission = setup_started(client)
    a = claim(client, "SIM-A")
    b = claim(client, "SIM-B")
    assert a.status_code == 200 and b.status_code == 200
    ja, jb = a.json()["job"], b.json()["job"]
    assert ja["task_key"] == "TASK-A"
    assert jb["task_key"] == "TASK-B"
    assert "claim_token" in ja and len(ja["claim_token"]) == 64
    assert ja["physical_command_authorized"] is False
    assert claim(client, "SIM-A").json()["job"] is None
    assert ack(client, "SIM-A", jb, "ACCEPTED_SIM").status_code == 403
    assert ack(client, "SIM-A", ja, "COMPLETED_SIM").status_code == 409
    ack_id = uuid.uuid4().hex
    assert ack(client, "SIM-A", ja, "ACCEPTED_SIM", event_id=ack_id).status_code == 200
    duplicate = ack(client, "SIM-A", ja, "ACCEPTED_SIM", event_id=ack_id)
    assert duplicate.status_code == 200 and duplicate.json()["duplicate"] is True
    assert ack(client, "SIM-A", ja, "COMPLETED_SIM").status_code == 200
    assert ack(client, "SIM-B", jb, "ACCEPTED_SIM").status_code == 200
    assert ack(client, "SIM-B", jb, "COMPLETED_SIM").status_code == 200
    record = client.get("/api/v1/missions/" + mission["id"],
                        headers=jwt_header()).json()
    assert record["status"] == "COMPLETED_SIM"
    assert all(row["status"] == "COMPLETED_SIM" for row in record["jobs"])
    assert client.get("/api/v1/events", headers=jwt_header()).status_code == 200
    assert client.get("/api/v1/events", headers=jwt_header(scopes="fleet:read")).status_code == 403


def test_lease_fencing_expiry_and_no_redelivery(client):
    mission = setup_started(client)
    job = claim(client, "SIM-A").json()["job"]
    with Session(client.app.state.engine) as session:
        with session.begin():
            lease = session.scalar(select(Lease).where(Lease.tenant == "TENANT-A",
                        Lease.vehicle_id == session.scalar(select(Job).where(
                            Job.id == job["id"])).vehicle_id))
            lease.expires_at = time.time() - 1
    swept = client.post("/api/v1/internal/sweep", headers=jwt_header())
    assert swept.status_code == 200
    state = client.get("/api/v1/missions/" + mission["id"],
                       headers=jwt_header()).json()
    assert state["status"] == "LEASE_LOST"
    assert ack(client, "SIM-A", job, "ACCEPTED_SIM").status_code == 409
    assert claim(client, "SIM-A").json()["job"] is None


def test_missing_device_ack_expires_and_never_auto_retries(client):
    mission = setup_started(client)
    job = claim(client, "SIM-A").json()["job"]
    with Session(client.app.state.engine) as session:
        with session.begin():
            rec = session.get(Job, job["id"])
            rec.deadline = time.time() - 1
    assert client.post("/api/v1/internal/sweep", headers=jwt_header()).status_code == 200
    status = client.get("/api/v1/missions/" + mission["id"],
                        headers=jwt_header()).json()
    assert status["status"] == "FAILED_SIM"
    assert claim(client, "SIM-A").json()["job"] is None
    assert ack(client, "SIM-A", job, "ACCEPTED_SIM").status_code == 409


def test_rejected_job_fails_entire_plan_and_does_not_erase_audit(client):
    mission = setup_started(client)
    job = claim(client, "SIM-A").json()["job"]
    assert ack(client, "SIM-A", job, "REJECTED_SIM").status_code == 200
    status = client.get("/api/v1/missions/" + mission["id"],
                        headers=jwt_header()).json()
    assert status["status"] == "FAILED_SIM"
    assert client.get("/api/v1/events", headers=jwt_header()).json()["events"]


def test_production_configuration_rejects_test_keys_and_non_postgres(monkeypatch):
    monkeypatch.setenv("AEROHELM_CLOUD_MODE", "production")
    monkeypatch.setenv("AEROHELM_DATABASE_URL", "sqlite:///unsafe.db")
    monkeypatch.setenv("AEROHELM_TEST_JWT_SECRET", TEST_SECRET)
    with pytest.raises(ValueError):
        Settings.from_env()


def test_persistent_state_survives_api_restart(tmp_path, monkeypatch):
    monkeypatch.setenv("AEROHELM_VIRTUAL_DISPATCH_ENABLED", "true")
    database = "sqlite+pysqlite:///" + str(tmp_path / "cloud.db")
    with TestClient(create_app(config(database))) as first:
        assert register(first, "SIM-A").status_code == 201
        task = spec(("SIM-A",))
        task["tasks"] = task["tasks"][:1]
        mission = draft(first, plan=task).json()
    with TestClient(create_app(config(database))) as second:
        rows = second.get("/api/v1/vehicles", headers=jwt_header()).json()["vehicles"]
        assert rows[0]["vehicle_id"] == "SIM-A"
        loaded = second.get("/api/v1/missions/" + mission["id"],
                            headers=jwt_header()).json()
        assert loaded["status"] == "DRAFT"
        assert loaded["plan_sha256"] == mission["plan_sha256"]
