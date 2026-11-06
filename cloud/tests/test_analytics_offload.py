"""Cloud-compute offload: tenant isolation, queue idempotency and worker receipts.

All tests use a software-only ASGI client and disposable SQLite.
No Windows GCS, Pixhawk or telemetry-radio connection occurs.
"""
from __future__ import annotations

import time
import uuid

from fastapi.testclient import TestClient
from sqlalchemy.orm import Session

from cloud.app.analytics import compute_one
from cloud.app.main import create_app
from cloud.app.models import OutboxEvent
from cloud.tests.test_control_plane import (
    SCOPES, config, draft, jwt_header, register,
)
from cloud.tests.test_telemetry import dev, sample


def auth(actor="analyst", tenant="TENANT-A", scope=None):
    return jwt_header(actor, tenant=tenant,
                      scopes=scope or SCOPES + " analytics:submit")


def submit(client, kind="fleet_health", key=None, *, headers=None):
    return client.post("/api/v1/cloud-compute/jobs",
                       headers=headers or auth(),
                       json={"request_id": key or uuid.uuid4().hex,
                             "kind": kind})


def process(client):
    def now(db):
        return time.time()
    def emit(db, tenant, typ, aggregate, actor):
        db.add(OutboxEvent(tenant=tenant, at=now(db), event_type=typ,
                           aggregate_id=aggregate, body={"actor": actor}))
    with Session(client.app.state.engine) as session:
        with session.begin():
            return compute_one(session, now, emit)


def test_thin_client_submits_and_cloud_worker_aggregates_fleet():
    with TestClient(create_app(config())) as client:
        register(client, "SIM-A", 12.97)
        register(client, "PIX-01", kind="hardware")
        opened = client.post("/api/v1/device/telemetry/session",
                             headers=dev("PIX-01"))
        assert opened.status_code == 200
        assert client.post("/api/v1/device/telemetry",
                           headers=dev("PIX-01"),
                           json={**sample(opened.json()["session_epoch"], 1),
                                 "battery_pct": 12, "gps_fix_type": 1}
                           ).status_code == 202
        response = submit(client)
        assert response.status_code == 202
        report_id = response.json()["job_id"]
        assert response.json()["status"] == "QUEUED"
        # No CPU-heavy operation takes place in the client endpoint.
        assert client.get("/api/v1/cloud-compute/jobs/" + report_id,
                          headers=auth()).json()["result"] is None
        assert process(client) is True
        assert process(client) is False
        report = client.get("/api/v1/cloud-compute/jobs/" + report_id,
                            headers=auth()).json()
        assert report["status"] == "SUCCEEDED"
        assert report["physical_control_enabled"] is False
        assert report["result"]["vehicle_count"] == 2
        assert report["result"]["fresh_sources"] == 1
        px = next(v for v in report["result"]["vehicles"]
                  if v["vehicle_id"] == "PIX-01")
        assert px["fresh"] is True
        assert set(px["advisory_risks"]) == {
            "REPORTED_LOW_BATTERY", "REPORTED_GNSS_DEGRADED",
        }
        assert px["observation_provenance"] == "DEVICE_REPORTED_NOT_VERIFIED"
        assert client.get("/api/v1/cloud-compute/jobs/" + report_id,
                          headers=auth(actor="another")).status_code == 404
        assert client.get("/api/v1/cloud-compute/jobs/" + report_id,
                          headers=auth(tenant="TENANT-B")).status_code == 404


def test_portfolio_report_cloud_side_only():
    with TestClient(create_app(config())) as client:
        register(client, "SIM-A", 12.97)
        plan = {
            "request_id": uuid.uuid4().hex, "vehicle_ids": ["SIM-A"],
            "tasks": [{
                "key": "TASK-001", "latitude": 12.9701, "longitude":77.5901,
                "relative_altitude_m": 30, "required_energy_wh": 5,
                "priority": 7,
            }],
        }
        assert draft(client, plan=plan).status_code == 201
        response = submit(client, kind="mission_portfolio")
        assert response.status_code == 202
        assert process(client) is True
        result = client.get(
            "/api/v1/cloud-compute/jobs/" + response.json()["job_id"],
            headers=auth(),
        ).json()["result"]
        assert result["mission_states"] == {"DRAFT": 1}
        assert result["virtual_job_states"] == {"PLANNED": 1}
        assert result["mission_count"] == 1
        assert result["job_count"] == 1
        assert result["flight_authorized"] is False


def test_analytics_idempotency_and_invalid_action_blocked():
    with TestClient(create_app(config())) as client:
        key = uuid.uuid4().hex
        first = submit(client, key=key)
        assert first.status_code == 202
        retry = submit(client, key=key)
        assert retry.json()["job_id"] == first.json()["job_id"]
        assert submit(client, "mission_portfolio", key=key).status_code == 409
        assert client.post("/api/v1/cloud-compute/jobs",
                           headers=auth(),
                           json={"request_id":uuid.uuid4().hex,
                                 "kind":"arm_aircraft"}).status_code == 422
        assert client.post("/api/v1/cloud-compute/jobs",
                           headers=auth(),
                           json={"request_id":uuid.uuid4().hex,
                                 "kind":"fleet_health",
                                 "payload":{"shell":"rm -rf"}}).status_code == 422
        assert submit(client, headers=auth(scope="fleet:read")).status_code == 403
        assert client.get("/api/v1/cloud-compute/jobs/"+first.json()["job_id"],
                          headers=auth(scope="analytics:submit")).status_code == 403


def test_analytics_pending_capacity_cannot_exhaust_cloud_db():
    with TestClient(create_app(config())) as client:
        for index in range(16):
            assert submit(client).status_code == 202
        assert submit(client).status_code == 429
        assert process(client) is True
        assert submit(client).status_code == 202


def test_cloud_compute_never_requires_gcs_or_aircraft_imports():
    from pathlib import Path
    root = Path(__file__).resolve().parents[1]
    sources = "\n".join((root/"app"/name).read_text() for name in (
        "analytics.py", "compute_worker.py",
    ))
    for forbidden in (
        "mavutil", "mavlink_connection(", "Serial(", "command_long_send(",
        "param_set_send(", "os.system(", "subprocess.run(", "eval(",
    ):
        assert forbidden not in sources


def test_completed_reports_expire_without_erasing_audit_or_mission_state():
    from sqlalchemy import func, select
    from cloud.app.analytics import prune_expired_reports, REPORT_RETENTION_SECONDS
    from cloud.app.models import CloudComputeJob
    with TestClient(create_app(config())) as client:
        result = submit(client)
        assert result.status_code == 202
        report_id = result.json()["job_id"]
        assert process(client) is True
        with Session(client.app.state.engine) as db:
            with db.begin():
                row = db.get(CloudComputeJob, report_id)
                row.finished_at = time.time() - REPORT_RETENTION_SECONDS - 5
        with Session(client.app.state.engine) as db:
            with db.begin():
                audit_before = db.scalar(select(func.count(OutboxEvent.id)))
                prune_expired_reports(db, time.time())
        assert client.get("/api/v1/cloud-compute/jobs/" + report_id,
                          headers=auth()).status_code == 404
        with Session(client.app.state.engine) as db:
            audit_after = db.scalar(select(func.count(OutboxEvent.id)))
            assert audit_after == audit_before
