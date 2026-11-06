"""PostgreSQL-only proof that two cloud workers cannot claim one report."""
from __future__ import annotations

import os
import time
import uuid
from concurrent.futures import ThreadPoolExecutor

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import create_engine, func, select
from sqlalchemy.orm import Session

from cloud.app.analytics import compute_one
from cloud.app.main import create_app
from cloud.app.models import CloudComputeJob, OutboxEvent
from cloud.tests.test_control_plane import SCOPES, config, jwt_header

POSTGRES = os.getenv("AEROHELM_DATABASE_URL", "")
pytestmark = pytest.mark.skipif(
    not POSTGRES.startswith("postgresql+psycopg://"),
    reason="Requires a migrated disposable PostgreSQL CI database",
)


def test_independent_cloud_workers_claim_each_analytics_job_once():
    tenant = "TENANT-ANALYTICS-" + uuid.uuid4().hex[:12]
    request_id = uuid.uuid4().hex
    headers = jwt_header("analyst", tenant=tenant,
                         scopes=SCOPES + " analytics:submit")
    with TestClient(create_app(config(POSTGRES))) as client:
        response = client.post("/api/v1/cloud-compute/jobs",
                               headers=headers,
                               json={"request_id": request_id,
                                     "kind": "mission_portfolio"})
        assert response.status_code == 202, response.text
        job_id = response.json()["job_id"]

        def process(_):
            engine = create_engine(POSTGRES, pool_pre_ping=True)
            try:
                with Session(engine) as db:
                    with db.begin():
                        def now(_db):
                            return time.time()
                        def audit(sess, audit_tenant, kind, aggregate, actor):
                            sess.add(OutboxEvent(
                                tenant=audit_tenant, at=now(sess),
                                event_type=kind, aggregate_id=aggregate,
                                body={"actor": actor}))
                        return compute_one(db, now, audit)
            finally:
                engine.dispose()

        with ThreadPoolExecutor(max_workers=2) as pool:
            outcomes = list(pool.map(process, range(2)))
        assert sorted(outcomes) == [False, True]
        status = client.get("/api/v1/cloud-compute/jobs/" + job_id,
                            headers=headers).json()
        assert status["status"] == "SUCCEEDED"
        with Session(client.app.state.engine) as db:
            count = db.scalar(select(func.count(OutboxEvent.id)).where(
                OutboxEvent.aggregate_id == job_id,
                OutboxEvent.event_type == "cloud_compute.succeeded"))
            assert count == 1
