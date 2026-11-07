"""Verify expired work does not create new device work after restart."""
import time

from fastapi.testclient import TestClient
from sqlalchemy import select
from sqlalchemy.orm import Session

from cloud.app.main import create_app
from cloud.app.models import Job
from cloud.tests.test_control_plane import (
    acquire, approve, claim, config, draft, jwt_header, register, start,
)


def test_reusable_scheduler_marks_expired_claim_terminal(monkeypatch):
    monkeypatch.setenv("AEROHELM_VIRTUAL_DISPATCH_ENABLED", "true")
    with TestClient(create_app(config())) as client:
        register(client, "SIM-A", 12.97)
        plan = {"request_id": "a" * 32, "vehicle_ids": ["SIM-A"], "tasks": [
            {"key":"TASK-01","latitude":12.9701,"longitude":77.5901,
             "relative_altitude_m":30,"required_energy_wh":2,"priority":5}]}
        mission = draft(client, plan=plan).json()
        approve(client, mission)
        acquire(client, "SIM-A")
        start(client, mission)
        job = claim(client, "SIM-A").json()["job"]
        with Session(client.app.state.engine) as session:
            with session.begin():
                session.get(Job, job["id"]).deadline = time.time() - 10
        with Session(client.app.state.engine) as session:
            with session.begin():
                client.app.state.sweep_expired_work(session)
        record = client.get("/api/v1/missions/" + mission["id"],
                            headers=jwt_header()).json()
        assert record["status"] == "FAILED_SIM"
        assert claim(client, "SIM-A").json()["job"] is None


def test_missing_claim_deadline_fails_closed_without_scheduler_crash(monkeypatch):
    monkeypatch.setenv("AEROHELM_VIRTUAL_DISPATCH_ENABLED", "true")
    with TestClient(create_app(config())) as client:
        register(client, "SIM-B", 12.97)
        plan = {"request_id": "b" * 32, "vehicle_ids": ["SIM-B"], "tasks": [
            {"key": "TASK-02", "latitude":12.9701, "longitude":77.5901,
             "relative_altitude_m":30, "required_energy_wh":2, "priority":4}]}
        mission = draft(client, plan=plan).json()
        assert approve(client, mission).status_code == 200
        assert acquire(client, "SIM-B").status_code == 200
        assert start(client, mission).status_code == 202
        job = claim(client, "SIM-B").json()["job"]
        with Session(client.app.state.engine) as session:
            with session.begin():
                session.get(Job, job["id"]).deadline = None
        with Session(client.app.state.engine) as session:
            with session.begin():
                client.app.state.sweep_expired_work(session)
        status = client.get("/api/v1/missions/" + mission["id"],
                            headers=jwt_header()).json()
        assert status["status"] == "FAILED_SIM"
        assert claim(client, "SIM-B").json()["job"] is None
