"""AeroHelm independent fleet control plane.

Production architecture boundary:
- authenticated, tenant-scoped fleet registry / durable workflow state;
- two-person mission approval, database-backed exclusive virtual leases;
- transactional job inbox/outbox with epoch fencing and expiry;
- simulator-only device delivery; no hardware flight-control operations.

This service neither reads a Pixhawk UART nor imports Mission Planner/MAVLink.
Physical vehicles may register for later observation but cannot be dispatched.
"""
from __future__ import annotations

from contextlib import asynccontextmanager
import hashlib
import hmac
import os
import secrets
import time
import uuid
from typing import Literal

from fastapi import Depends, FastAPI, HTTPException
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy import create_engine, func, select, text
from sqlalchemy.orm import Session
from sqlalchemy.pool import StaticPool

from .config import Settings
from .models import (AckRecord, Base, Job, Lease, Mission, OutboxEvent,
                     RequestRecord, Vehicle)
from .planner import MissionSpec, VehicleProfile, allocate, digest
from .security import IDENTIFIER, Principal, TokenVerifier

security = HTTPBearer(auto_error=False)
RUNNING = "RUNNING_SIM"
JOB_TERMINAL = {"COMPLETED_SIM", "REJECTED_SIM", "TIMED_OUT", "LEASE_LOST", "CANCELLED_SIM"}
CLAIM_SECONDS = 10
ACCEPT_SECONDS = 30


class VehicleCreate(BaseModel):
    model_config = ConfigDict(extra="forbid")
    vehicle_id: str = Field(min_length=3, max_length=64, pattern=r"^[A-Za-z0-9_-]+$")
    kind: Literal["virtual", "hardware"]
    profile: VehicleProfile


class LeaseRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    duration_seconds: int = Field(ge=10, le=120)


class RenewRequest(LeaseRequest):
    expected_epoch: int = Field(ge=1)


class Approval(BaseModel):
    model_config = ConfigDict(extra="forbid")
    request_id: str = Field(pattern=r"^[0-9a-f]{32}$")
    plan_sha256: str = Field(pattern=r"^[a-f0-9]{64}$")


class StartRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    request_id: str = Field(pattern=r"^[0-9a-f]{32}$")


class AckIn(BaseModel):
    model_config = ConfigDict(extra="forbid")
    event_id: str = Field(pattern=r"^[0-9a-f]{32}$")
    claim_token: str = Field(pattern=r"^[a-f0-9]{64}$")
    phase: Literal["ACCEPTED_SIM", "COMPLETED_SIM", "REJECTED_SIM"]


def create_app(settings: Settings | None = None) -> FastAPI:
    cfg = settings or Settings.from_env()
    if cfg.mode == "test" and cfg.database_url.startswith("sqlite") and ":memory:" in cfg.database_url:
        engine = create_engine(cfg.database_url, connect_args={"check_same_thread": False},
                               poolclass=StaticPool)
    else:
        engine = create_engine(cfg.database_url, pool_pre_ping=True, pool_recycle=1800)
    verifier = TokenVerifier(cfg)
    virtual_dispatch = os.getenv("AEROHELM_VIRTUAL_DISPATCH_ENABLED", "false").lower() == "true"

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        if cfg.mode == "test":
            Base.metadata.create_all(engine)
        else:
            # Migrations are a separate deployment step. Fail startup rather
            # than silently create production tables or start without schema.
            with engine.connect() as connection:
                for name in ("vehicles", "missions", "jobs", "control_leases",
                             "idempotency", "job_ack_inbox", "event_outbox"):
                    connection.execute(text("SELECT 1 FROM " + name + " LIMIT 1"))
        try:
            yield
        finally:
            engine.dispose()

    app = FastAPI(title="AeroHelm Cloud Fleet Control Plane",
                  version="1.0.0-rc1", lifespan=lifespan)
    app.state.engine = engine
    app.state.dispatch_enabled = virtual_dispatch

    def database():
        with Session(engine, expire_on_commit=False) as session:
            yield session

    def identity(credentials: HTTPAuthorizationCredentials | None = Depends(security)):
        if credentials is None or credentials.scheme.lower() != "bearer":
            raise HTTPException(401, "Bearer authentication required")
        return verifier.verify(credentials.credentials)

    def now(db: Session) -> float:
        if db.bind.dialect.name == "postgresql":
            return float(db.scalar(select(func.extract("epoch", func.clock_timestamp()))))
        return time.time()  # test-only SQLite

    def emit(db: Session, tenant: str, typ: str, aggregate: str, actor: str) -> None:
        # Transactional outbox; no credentials, token claims or raw UAV data.
        db.add(OutboxEvent(tenant=tenant, at=now(db), event_type=typ,
                           aggregate_id=aggregate, body={"actor": actor, "mode": "SIM_ONLY"}))

    def scoped_vehicle(db: Session, tenant: str, ident: str, *, lock=False) -> Vehicle:
        q = select(Vehicle).where(Vehicle.tenant == tenant, Vehicle.name == ident)
        if lock:
            q = q.with_for_update()
        row = db.scalar(q)
        if row is None:
            raise HTTPException(404, "Vehicle not found")
        return row

    def scoped_mission(db: Session, tenant: str, ident: str, *, lock=False) -> Mission:
        q = select(Mission).where(Mission.id == ident, Mission.tenant == tenant)
        if lock:
            q = q.with_for_update()
        row = db.scalar(q)
        if row is None:
            raise HTTPException(404, "Mission not found")
        return row

    def jobs_for(db: Session, mission_id: str) -> list[Job]:
        return list(db.scalars(select(Job).where(Job.mission_id == mission_id)
                               .order_by(Job.task_key)).all())

    def show_job(job: Job) -> dict:
        return {
            "id": job.id, "task_key": job.task_key, "status": job.status,
            "lease_epoch": job.lease_epoch, "deadline": job.deadline,
            "updated_at": job.updated_at,
        }

    def show_mission(db: Session, mission: Mission) -> dict:
        return {
            "id": mission.id, "status": mission.status,
            "plan_sha256": mission.digest, "created_by": mission.creator,
            "approved_by": mission.approver, "specification": mission.specification,
            "jobs": [show_job(job) for job in jobs_for(db, mission.id)],
            "physical_flight_authorized": False,
            "execution_target": "VIRTUAL_SIMULATOR_ONLY",
        }

    def release(db: Session, mission: Mission, final_state: str) -> None:
        mission.status = final_state
        mission.updated_at = now(db)
        for job in jobs_for(db, mission.id):
            if job.status not in JOB_TERMINAL:
                job.status = "LEASE_LOST" if final_state == "LEASE_LOST" else "CANCELLED_SIM"
                job.updated_at = mission.updated_at
        for name in mission.specification["vehicle_ids"]:
            row = scoped_vehicle(db, mission.tenant, name, lock=True)
            if row.active_mission == mission.id:
                row.active_mission = None

    def sweep(db: Session):
        """Durable expiry: never replay expired or unknown jobs after restart."""
        timestamp = now(db)
        active = list(db.scalars(select(Mission).where(Mission.status == RUNNING)
                                 .with_for_update(skip_locked=True)).all())
        for mission in active:
            jobs = jobs_for(db, mission.id)
            lost = False
            for job in jobs:
                lease = db.get(Lease, job.vehicle_id)
                if (lease is None or lease.expires_at <= timestamp or
                        job.lease_epoch != lease.epoch):
                    lost = True
                    break
            if lost:
                release(db, mission, "LEASE_LOST")
                emit(db, mission.tenant, "mission.lease_lost", mission.id, "system")
                continue
            if any(job.status in ("CLAIMED_SIM", "ACCEPTED_SIM") and
                   (job.deadline is None or job.deadline <= timestamp)
                   for job in jobs):
                for job in jobs:
                    if job.status in ("CLAIMED_SIM", "ACCEPTED_SIM") and job.deadline <= timestamp:
                        job.status = "TIMED_OUT"
                release(db, mission, "FAILED_SIM")
                emit(db, mission.tenant, "mission.timeout", mission.id, "system")

    def request_seen(db: Session, principal: Principal, key: str,
                     operation: str, payload_sha: str) -> RequestRecord | None:
        previous = db.scalar(select(RequestRecord).where(
            RequestRecord.tenant == principal.tenant,
            RequestRecord.actor == principal.subject,
            RequestRecord.key == key).with_for_update())
        if previous and (previous.operation != operation or previous.digest != payload_sha):
            raise HTTPException(409, "Idempotency key used for a different operation")
        return previous

    def remember(db: Session, principal: Principal, key: str, operation: str,
                 payload_sha: str, resource: str) -> None:
        db.add(RequestRecord(id=str(uuid.uuid4()), tenant=principal.tenant,
                             actor=principal.subject, key=key, operation=operation,
                             digest=payload_sha, resource=resource))

    @app.get("/health/live")
    def live():
        return {"alive": True}

    @app.get("/health/ready")
    def ready(db: Session = Depends(database)):
        try:
            db.execute(text("SELECT 1"))
        except Exception:
            raise HTTPException(503, "Database unavailable") from None
        return {"ready": True}

    @app.get("/api/v1/capabilities")
    def capabilities(principal: Principal = Depends(identity)):
        principal.require("fleet:read")
        return {
            "cloud_fleet_control_plane": True,
            "physical_aircraft_dispatch": False,
            "virtual_dispatch_enabled": virtual_dispatch,
            "durable_state": True,
            "multi_tenant": True,
            "OIDC": cfg.mode == "production",
            "edge_acks_are_flight_evidence": False,
            "deployment_readiness": "REQUIRES_INDEPENDENT_ACCEPTANCE",
        }

    @app.post("/api/v1/vehicles", status_code=201)
    def register_vehicle(body: VehicleCreate, p: Principal = Depends(identity),
                         db: Session = Depends(database)):
        p.require("fleet:admin")
        with db.begin():
            prior = db.scalar(select(Vehicle).where(
                Vehicle.tenant == p.tenant, Vehicle.name == body.vehicle_id))
            if prior:
                raise HTTPException(409, "Vehicle already registered")
            vehicle = Vehicle(id=str(uuid.uuid4()), tenant=p.tenant, name=body.vehicle_id,
                              kind=body.kind, profile=body.profile.model_dump(),
                              created_at=now(db))
            db.add(vehicle)
            emit(db, p.tenant, "vehicle.registered", vehicle.id, p.subject)
        return {"vehicle_id":vehicle.name, "kind":vehicle.kind,
                "dispatch_allowed": False, "profile_verified": False}

    @app.get("/api/v1/vehicles")
    def list_vehicles(p: Principal = Depends(identity), db: Session = Depends(database)):
        p.require("fleet:read")
        rows = db.scalars(select(Vehicle).where(Vehicle.tenant == p.tenant)
                          .order_by(Vehicle.name)).all()
        return {"vehicles": [
            {"vehicle_id": v.name, "kind": v.kind, "profile": v.profile,
             "active_mission": v.active_mission,
             "physical_control_enabled": False} for v in rows
        ]}

    @app.post("/api/v1/vehicles/{vehicle_id}/lease")
    def acquire_lease(vehicle_id: str, body: LeaseRequest,
                      p: Principal = Depends(identity), db: Session = Depends(database)):
        p.require("fleet:operate")
        with db.begin():
            sweep(db)
            vehicle = scoped_vehicle(db, p.tenant, vehicle_id, lock=True)
            if vehicle.kind != "virtual":
                raise HTTPException(403, "Hardware control leases are disabled")
            if vehicle.active_mission:
                raise HTTPException(409, "Virtual vehicle already executing")
            existing = db.get(Lease, vehicle.id)
            timestamp = now(db)
            if existing and existing.expires_at > timestamp and existing.holder != p.subject:
                raise HTTPException(409, "Lease held by a different operator")
            if existing is None:
                existing = Lease(vehicle_id=vehicle.id, tenant=p.tenant,
                                 holder=p.subject, epoch=1, expires_at=timestamp + body.duration_seconds)
                db.add(existing)
            else:
                existing.holder = p.subject
                existing.epoch += 1  # fence all previously issued claims
                existing.expires_at = timestamp + body.duration_seconds
            emit(db, p.tenant, "lease.acquired", vehicle.id, p.subject)
            return {"vehicle_id":vehicle.name, "epoch":existing.epoch,
                    "expires_at":existing.expires_at}

    @app.post("/api/v1/vehicles/{vehicle_id}/lease/renew")
    def renew_lease(vehicle_id: str, body: RenewRequest,
                    p: Principal = Depends(identity), db: Session = Depends(database)):
        p.require("fleet:operate")
        with db.begin():
            sweep(db)
            vehicle = scoped_vehicle(db, p.tenant, vehicle_id, lock=True)
            current = db.get(Lease, vehicle.id)
            timestamp = now(db)
            if (not current or current.epoch != body.expected_epoch or
                    current.holder != p.subject or current.expires_at <= timestamp):
                raise HTTPException(409, "Lease expired, changed or not held")
            current.expires_at = timestamp + body.duration_seconds
            emit(db, p.tenant, "lease.renewed", vehicle.id, p.subject)
            return {"vehicle_id":vehicle.name, "epoch":current.epoch,
                    "expires_at":current.expires_at}

    @app.post("/api/v1/missions", status_code=201)
    def create_mission(body: MissionSpec, p: Principal = Depends(identity),
                       db: Session = Depends(database)):
        p.require("mission:write")
        payload = body.model_dump(mode="json")
        payload_sha = digest(payload)
        with db.begin():
            previous = request_seen(db, p, body.request_id, "create", payload_sha)
            if previous:
                return show_mission(db, scoped_mission(db, p.tenant, previous.resource))
            profiles = {}
            vehicles = {}
            for ident in sorted(body.vehicle_ids):
                vehicle = scoped_vehicle(db, p.tenant, ident)
                if vehicle.kind != "virtual":
                    raise HTTPException(403, "Hardware mission dispatch is disabled")
                profiles[ident] = VehicleProfile.model_validate(vehicle.profile)
                vehicles[ident] = vehicle
            try:
                assigned = allocate(body, profiles)
            except ValueError:
                raise HTTPException(422, "Unassignable fleet plan") from None
            spec = {"vehicle_ids": sorted(body.vehicle_ids),
                    "tasks": [t.model_dump() for t in body.tasks],
                    "profiles": {k: v.model_dump() for k, v in sorted(profiles.items())},
                    "assignments": assigned,
                    "evidence": {"airspace": "NOT_EVALUATED",
                                 "weather": "NOT_EVALUATED", "terrain": "NOT_EVALUATED",
                                 "energy": "OPERATOR_ESTIMATE"},
                    "execution_target": "VIRTUAL_SIMULATOR_ONLY"}
            plan_sha = digest(spec)
            timestamp = now(db)
            mission = Mission(id=str(uuid.uuid4()), tenant=p.tenant, creator=p.subject,
                              status="DRAFT", digest=plan_sha, specification=spec,
                              created_at=timestamp, updated_at=timestamp)
            db.add(mission)
            for item in assigned:
                vehicle = vehicles[item["vehicle_id"]]
                db.add(Job(id=str(uuid.uuid4()), tenant=p.tenant, mission_id=mission.id,
                           vehicle_id=vehicle.id, task_key=item["task_key"],
                           payload=item["task"], status="PLANNED",
                           updated_at=timestamp))
            remember(db, p, body.request_id, "create", payload_sha, mission.id)
            emit(db, p.tenant, "mission.drafted", mission.id, p.subject)
            db.flush()
            return show_mission(db, mission)

    @app.get("/api/v1/missions/{mission_id}")
    def get_mission(mission_id: uuid.UUID, p: Principal = Depends(identity),
                    db: Session = Depends(database)):
        p.require("fleet:read")
        return show_mission(db, scoped_mission(db, p.tenant, str(mission_id)))

    @app.post("/api/v1/missions/{mission_id}/approve")
    def approve_mission(mission_id: uuid.UUID, body: Approval,
                        p: Principal = Depends(identity), db: Session = Depends(database)):
        p.require("mission:approve")
        payload_sha = digest({"mission": str(mission_id), "digest": body.plan_sha256})
        with db.begin():
            mission = scoped_mission(db, p.tenant, str(mission_id), lock=True)
            if p.subject == mission.creator:
                raise HTTPException(403, "Two-person approval required")
            if not hmac.compare_digest(mission.digest, body.plan_sha256):
                raise HTTPException(409, "Mission digest differs from reviewed plan")
            previous = request_seen(db, p, body.request_id, "approve", payload_sha)
            if previous:
                return show_mission(db, mission)
            if mission.status != "DRAFT":
                raise HTTPException(409, "Mission is not a draft")
            mission.status = "APPROVED_SIM"
            mission.approver = p.subject
            mission.updated_at = now(db)
            remember(db, p, body.request_id, "approve", payload_sha, mission.id)
            emit(db, p.tenant, "mission.approved", mission.id, p.subject)
            db.flush()
            return show_mission(db, mission)

    @app.post("/api/v1/missions/{mission_id}/start", status_code=202)
    def start_mission(mission_id: uuid.UUID, body: StartRequest,
                      p: Principal = Depends(identity), db: Session = Depends(database)):
        p.require("mission:execute")
        if not virtual_dispatch:
            raise HTTPException(503, "Virtual dispatch feature gate is off")
        payload_sha = digest({"mission": str(mission_id), "operation": "start"})
        with db.begin():
            sweep(db)
            mission = scoped_mission(db, p.tenant, str(mission_id), lock=True)
            previous = request_seen(db, p, body.request_id, "start", payload_sha)
            if previous:
                return show_mission(db, mission)
            if mission.status != "APPROVED_SIM":
                raise HTTPException(409, "Mission not approved")
            ids = {}
            for ident in sorted(mission.specification["vehicle_ids"]):
                vehicle = scoped_vehicle(db, p.tenant, ident, lock=True)
                lease = db.get(Lease, vehicle.id)
                if (vehicle.kind != "virtual" or vehicle.active_mission or
                        lease is None or lease.holder != p.subject or
                        lease.expires_at <= now(db)):
                    raise HTTPException(409, "Missing exclusive virtual vehicle authority")
                ids[vehicle.id] = (vehicle, lease.epoch)
            for vehicle, _ in ids.values():
                vehicle.active_mission = mission.id
            for job in jobs_for(db, mission.id):
                job.lease_epoch = ids[job.vehicle_id][1]
                job.status = "QUEUED_SIM"
                job.updated_at = now(db)
            mission.status = RUNNING
            mission.updated_at = now(db)
            remember(db, p, body.request_id, "start", payload_sha, mission.id)
            emit(db, p.tenant, "mission.started_sim", mission.id, p.subject)
            db.flush()
            return show_mission(db, mission)

    @app.post("/api/v1/missions/{mission_id}/abort")
    def abort_mission(mission_id: uuid.UUID, p: Principal = Depends(identity),
                      db: Session = Depends(database)):
        p.require("mission:execute")
        with db.begin():
            mission = scoped_mission(db, p.tenant, str(mission_id), lock=True)
            if mission.status == "ABORTED_SIM":
                return show_mission(db, mission)
            if mission.status not in ("DRAFT", "APPROVED_SIM", RUNNING):
                raise HTTPException(409, "Mission already terminal")
            release(db, mission, "ABORTED_SIM")
            emit(db, p.tenant, "mission.abort_recorded", mission.id, p.subject)
            db.flush()
            return show_mission(db, mission)

    @app.post("/api/v1/device/jobs/claim")
    def claim_job(p: Principal = Depends(identity), db: Session = Depends(database)):
        p.require("jobs:claim", kind="device")
        if not virtual_dispatch:
            raise HTTPException(503, "Virtual dispatch feature gate is off")
        with db.begin():
            sweep(db)
            vehicle = scoped_vehicle(db, p.tenant, p.vehicle_id, lock=True)
            if vehicle.kind != "virtual" or not vehicle.active_mission:
                return {"job": None}
            lease = db.get(Lease, vehicle.id)
            if lease is None or lease.expires_at <= now(db):
                return {"job": None}
            candidates = list(db.scalars(
                select(Job).where(
                    Job.tenant == p.tenant, Job.vehicle_id == vehicle.id,
                    Job.mission_id == vehicle.active_mission,
                    Job.status == "QUEUED_SIM").order_by(Job.task_key)
                .with_for_update(skip_locked=True)).all())
            if not candidates:
                return {"job": None}
            job = candidates[0]
            if job.lease_epoch != lease.epoch:
                raise HTTPException(409, "Stale authority epoch")
            token = secrets.token_hex(32)
            job.claim_digest = hashlib.sha256(token.encode()).hexdigest()
            job.status = "CLAIMED_SIM"
            job.deadline = now(db) + CLAIM_SECONDS
            job.updated_at = now(db)
            emit(db, p.tenant, "job.claimed_sim", job.id, p.subject)
            return {"job": {
                "id": job.id, "mission_id": job.mission_id,
                "task_key": job.task_key, "payload": job.payload,
                "lease_epoch": job.lease_epoch, "deadline": job.deadline,
                "claim_token": token, "execution_target": "VIRTUAL_SIMULATOR_ONLY",
                "physical_command_authorized": False,
            }}

    @app.post("/api/v1/device/jobs/{job_id}/ack")
    def ack_job(job_id: uuid.UUID, body: AckIn, p: Principal = Depends(identity),
                db: Session = Depends(database)):
        p.require("jobs:ack", kind="device")
        if not virtual_dispatch:
            raise HTTPException(503, "Virtual dispatch feature gate is off")
        with db.begin():
            job = db.scalar(select(Job).where(Job.id == str(job_id),
                Job.tenant == p.tenant).with_for_update())
            if job is None:
                raise HTTPException(404, "Job not found")
            vehicle = scoped_vehicle(db, p.tenant, p.vehicle_id, lock=True)
            if job.vehicle_id != vehicle.id or vehicle.kind != "virtual":
                raise HTTPException(403, "Device is not the assigned virtual vehicle")
            if not job.claim_digest or not hmac.compare_digest(
                job.claim_digest, hashlib.sha256(body.claim_token.encode()).hexdigest()
            ):
                raise HTTPException(403, "Invalid claim capability")
            fingerprint = digest({"phase": body.phase, "job": str(job_id)})
            existing = db.scalar(select(AckRecord).where(
                AckRecord.job_id == job.id, AckRecord.event_id == body.event_id))
            if existing:
                if existing.digest != fingerprint:
                    raise HTTPException(409, "ACK event ID reused with different contents")
                return {"job_id": job.id, "status": existing.phase,
                        "duplicate": True, "physical_command_authorized": False}
            sweep(db)
            mission = scoped_mission(db, p.tenant, job.mission_id, lock=True)
            lease = db.get(Lease, vehicle.id)
            timestamp = now(db)
            if (mission.status != RUNNING or
                    lease is None or lease.expires_at <= timestamp or
                    lease.epoch != job.lease_epoch or
                    vehicle.active_mission != mission.id or
                    job.deadline is None or job.deadline <= timestamp):
                raise HTTPException(409, "Claim expired, revoked or not running")
            permitted = {
                "CLAIMED_SIM": {"ACCEPTED_SIM", "REJECTED_SIM"},
                "ACCEPTED_SIM": {"COMPLETED_SIM", "REJECTED_SIM"},
            }
            if body.phase not in permitted.get(job.status, set()):
                raise HTTPException(409, "Illegal job state transition")
            job.status = body.phase
            job.updated_at = timestamp
            job.deadline = timestamp + ACCEPT_SECONDS if body.phase == "ACCEPTED_SIM" else None
            db.add(AckRecord(id=str(uuid.uuid4()), job_id=job.id, event_id=body.event_id,
                             digest=fingerprint, phase=body.phase))
            emit(db, p.tenant, "job." + body.phase.lower(), job.id, p.subject)
            jobs = jobs_for(db, mission.id)
            db.flush()
            if body.phase == "REJECTED_SIM":
                release(db, mission, "FAILED_SIM")
                emit(db, p.tenant, "mission.failed_sim", mission.id, p.subject)
            elif all(j.status == "COMPLETED_SIM" for j in jobs):
                release(db, mission, "COMPLETED_SIM")
                emit(db, p.tenant, "mission.completed_sim", mission.id, p.subject)
            return {"job_id":job.id, "status": body.phase,
                    "duplicate": False, "physical_command_authorized": False}

    @app.get("/api/v1/events")
    def events(after: int = 0, limit: int = 100,
               p: Principal = Depends(identity), db: Session = Depends(database)):
        p.require("audit:read")
        if after < 0 or not 1 <= limit <= 200:
            raise HTTPException(422, "Invalid event cursor or limit")
        rows = db.scalars(select(OutboxEvent).where(
            OutboxEvent.tenant == p.tenant, OutboxEvent.id > after
        ).order_by(OutboxEvent.id).limit(limit)).all()
        return {"events": [
            {"sequence": e.id, "at": e.at, "type": e.event_type,
             "aggregate_id": e.aggregate_id, "body": e.body} for e in rows
        ], "next_after": rows[-1].id if rows else after}

    @app.post("/api/v1/internal/sweep")
    def sweep_endpoint(p: Principal = Depends(identity),
                       db: Session = Depends(database)):
        # A dedicated scheduler identity must hold this narrow operator scope.
        p.require("scheduler:tick")
        with db.begin():
            sweep(db)
        return {"sweep": "completed", "physical_control_enabled": False}

    return app
