"""AeroHelm cloud-only analytics delegation.

A thin GCS or phone requests and displays results; server-side workers
perform the potentially more expensive fleet joins and aggregations.
This is observational data processing only: no MAVLink, control, shell,
video processing, or user-defined code execution.
"""
from __future__ import annotations

import uuid
from typing import Callable, Literal

from fastapi import Depends, FastAPI, HTTPException
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy import delete, func, select
from sqlalchemy.orm import Session

from .models import CloudComputeJob, Job, Mission, TelemetryLatest, Vehicle
from .security import Principal

MAX_OUTSTANDING_PER_TENANT = 16
MAX_RETAINED_PER_TENANT = 512
REPORT_RETENTION_SECONDS = 86400


class ComputeRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    request_id: str = Field(pattern=r"^[0-9a-f]{32}$")
    kind: Literal["fleet_health", "mission_portfolio"]


def _fleet_health(db: Session, tenant: str, timestamp: float) -> dict:
    fleet = list(db.scalars(select(Vehicle).where(
        Vehicle.tenant == tenant).order_by(Vehicle.name).limit(128)).all())
    observations = {row.vehicle_id: row for row in db.scalars(
        select(TelemetryLatest).where(TelemetryLatest.tenant == tenant)).all()}
    details = []
    for vehicle in fleet:
        recent = observations.get(vehicle.id)
        age = None if recent is None or recent.received_at is None else max(
            0.0, timestamp - recent.received_at)
        fresh = age is not None and age <= 5.0
        obs = (recent.observation or {}) if recent else {}
        attitude_age = obs.get("attitude_age_ms")
        # Server-side freshness never trusts a received old device angle.
        attitude_fresh = (
            fresh and type(attitude_age) is int
            and 0 <= attitude_age <= 2500
            and obs.get("roll_deg") is not None
            and obs.get("pitch_deg") is not None
            and age * 1000 + attitude_age <= 2500
        )
        risks = []
        if not fresh:
            risks.append("TELEMETRY_STALE")
        if obs.get("battery_pct") is not None and obs["battery_pct"] <= 20:
            risks.append("REPORTED_LOW_BATTERY")
        if obs.get("gps_fix_type") is not None and obs["gps_fix_type"] < 3:
            risks.append("REPORTED_GNSS_DEGRADED")
        details.append({
            "vehicle_id": vehicle.name, "kind": vehicle.kind,
            "fresh": fresh, "age_s": round(age, 3) if age is not None else None,
            "reported_attitude_fresh": attitude_fresh,
            "advisory_risks": risks,
            "active_virtual_mission": vehicle.active_mission,
            "observation_provenance": (
                "SIMULATED" if vehicle.kind == "virtual"
                else "DEVICE_REPORTED_NOT_VERIFIED"),
        })
    return {
        "kind": "fleet_health", "generated_at": timestamp,
        "vehicles": details, "vehicle_count": len(details),
        "fresh_sources": sum(bool(row["fresh"]) for row in details),
        "device_reported_attitude_sources": sum(
            bool(row["reported_attitude_fresh"]) for row in details),
        "at_risk_count": sum(bool(row["advisory_risks"]) for row in details),
        "flight_authorized": False,
        "assessment": "NON_AUTHORITATIVE_CLOUD_OBSERVATION",
    }


def _mission_portfolio(db: Session, tenant: str, timestamp: float) -> dict:
    def tally(model, key):
        pairs = db.execute(select(key, func.count()).where(
            model.tenant == tenant).group_by(key)).all()
        return {str(label): int(count) for label, count in pairs}
    states = tally(Mission, Mission.status)
    jobs = tally(Job, Job.status)
    return {
        "kind": "mission_portfolio", "generated_at": timestamp,
        "mission_states": states, "virtual_job_states": jobs,
        "mission_count": sum(states.values()),
        "job_count": sum(jobs.values()),
        "plan_feasibility_verified": False,
        "flight_authorized": False,
        "assessment": "WORKFLOW_ONLY_NOT_AIRCRAFT_EXECUTION",
    }


def prune_expired_reports(db: Session, timestamp: float) -> None:
    """Delete only completed ephemeral reports; never mission/event evidence."""
    db.execute(delete(CloudComputeJob).where(
        CloudComputeJob.status.in_(("SUCCEEDED", "FAILED")),
        CloudComputeJob.finished_at.is_not(None),
        CloudComputeJob.finished_at < timestamp - REPORT_RETENTION_SECONDS,
    ))


def compute_one(db: Session, now: Callable[[Session], float],
                emit: Callable) -> bool:
    """Process one queued report in caller's atomic DB transaction.

    PostgreSQL SKIP LOCKED fences parallel workers. Crash before commit
    leaves QUEUED (safe to recompute because there are NO aircraft side
    effects). This method does not open a network listener.
    """
    query = (select(CloudComputeJob).where(
        CloudComputeJob.status == "QUEUED"
    ).order_by(CloudComputeJob.created_at, CloudComputeJob.id)
     .limit(1).with_for_update(skip_locked=True))
    job = db.scalar(query)
    if job is None:
        return False
    timestamp = now(db)
    try:
        if job.kind == "fleet_health":
            output = _fleet_health(db, job.tenant, timestamp)
        elif job.kind == "mission_portfolio":
            output = _mission_portfolio(db, job.tenant, timestamp)
        else:
            raise ValueError("Unsupported cloud report")
        job.status = "SUCCEEDED"
        job.result = output
    except (ValueError, TypeError, KeyError, OverflowError):
        job.status = "FAILED"
        job.result = {"error": "NON_RETRYABLE_ANALYTICS_FAILURE"}
    job.finished_at = timestamp
    emit(db, job.tenant, "cloud_compute." + job.status.lower(), job.id, "cloud-worker")
    return True


def register_cloud_compute(
    app: FastAPI, *, database: Callable, identity: Callable,
    now: Callable, tenant_write_lock: Callable, emit: Callable,
) -> None:
    """Operator endpoints only submit/poll jobs; processing stays server-side."""

    @app.post("/api/v1/cloud-compute/jobs", status_code=202)
    def submit_report(body: ComputeRequest, p: Principal = Depends(identity),
                      db: Session = Depends(database)):
        p.require("analytics:submit")
        with db.begin():
            tenant_write_lock(db, p.tenant)
            previous = db.scalar(select(CloudComputeJob).where(
                CloudComputeJob.tenant == p.tenant,
                CloudComputeJob.owner == p.subject,
                CloudComputeJob.request_id == body.request_id))
            if previous is not None:
                if previous.kind != body.kind:
                    raise HTTPException(409, "Request ID reused for different report")
                return {"job_id": previous.id, "status": previous.status,
                        "physical_control_enabled": False}
            inflight = db.scalar(select(func.count(CloudComputeJob.id)).where(
                CloudComputeJob.tenant == p.tenant,
                CloudComputeJob.status == "QUEUED"))
            retained = db.scalar(select(func.count(CloudComputeJob.id)).where(
                CloudComputeJob.tenant == p.tenant))
            if inflight >= MAX_OUTSTANDING_PER_TENANT:
                raise HTTPException(429, "Cloud analytics queue at capacity")
            if retained >= MAX_RETAINED_PER_TENANT:
                raise HTTPException(429, "Cloud analytics retention quota reached")
            item = CloudComputeJob(
                id=str(uuid.uuid4()), tenant=p.tenant, owner=p.subject,
                request_id=body.request_id, kind=body.kind, status="QUEUED",
                created_at=now(db), finished_at=None, result=None)
            db.add(item)
            emit(db, p.tenant, "cloud_compute.queued", item.id, p.subject)
            db.flush()
            return {"job_id": item.id, "status": item.status,
                    "physical_control_enabled": False}

    @app.get("/api/v1/cloud-compute/jobs/{job_id}")
    def read_report(job_id: uuid.UUID, p: Principal = Depends(identity),
                    db: Session = Depends(database)):
        p.require("fleet:read")
        item = db.scalar(select(CloudComputeJob).where(
            CloudComputeJob.id == str(job_id),
            CloudComputeJob.tenant == p.tenant,
            CloudComputeJob.owner == p.subject))
        if item is None:
            raise HTTPException(404, "Cloud report not found")
        return {
            "job_id": item.id, "kind": item.kind, "status": item.status,
            "result": item.result, "created_at": item.created_at,
            "finished_at": item.finished_at,
            "physical_control_enabled": False,
        }

    app.state.process_one_cloud_report = compute_one
