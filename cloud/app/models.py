"""Transactional PostgreSQL state: no in-memory mission or authority store."""
from __future__ import annotations

from sqlalchemy import (
    Boolean, CheckConstraint, Float, ForeignKey, Index, Integer, JSON,
    String, UniqueConstraint,
)
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column


class Base(DeclarativeBase):
    pass


class Vehicle(Base):
    __tablename__ = "vehicles"
    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    tenant: Mapped[str] = mapped_column(String(64), nullable=False)
    name: Mapped[str] = mapped_column(String(64), nullable=False)
    kind: Mapped[str] = mapped_column(String(12), nullable=False)  # virtual or hardware
    profile: Mapped[dict] = mapped_column(JSON, nullable=False)
    active_mission: Mapped[str | None] = mapped_column(String(36))
    created_at: Mapped[float] = mapped_column(Float, nullable=False)
    __table_args__ = (UniqueConstraint("tenant", "name"), Index("ix_vehicle_tenant", "tenant"))


class Lease(Base):
    __tablename__ = "control_leases"
    vehicle_id: Mapped[str] = mapped_column(String(36), ForeignKey("vehicles.id"), primary_key=True)
    tenant: Mapped[str] = mapped_column(String(64), nullable=False)
    holder: Mapped[str] = mapped_column(String(128), nullable=False)
    epoch: Mapped[int] = mapped_column(Integer, nullable=False)
    expires_at: Mapped[float] = mapped_column(Float, nullable=False)


class Mission(Base):
    __tablename__ = "missions"
    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    tenant: Mapped[str] = mapped_column(String(64), nullable=False)
    creator: Mapped[str] = mapped_column(String(128), nullable=False)
    approver: Mapped[str | None] = mapped_column(String(128))
    status: Mapped[str] = mapped_column(String(24), nullable=False)
    digest: Mapped[str] = mapped_column(String(64), nullable=False)
    specification: Mapped[dict] = mapped_column(JSON, nullable=False)
    created_at: Mapped[float] = mapped_column(Float, nullable=False)
    updated_at: Mapped[float] = mapped_column(Float, nullable=False)
    __table_args__ = (Index("ix_mission_tenant", "tenant"),)


class Job(Base):
    __tablename__ = "jobs"
    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    tenant: Mapped[str] = mapped_column(String(64), nullable=False)
    mission_id: Mapped[str] = mapped_column(String(36), ForeignKey("missions.id"), nullable=False)
    vehicle_id: Mapped[str] = mapped_column(String(36), ForeignKey("vehicles.id"), nullable=False)
    task_key: Mapped[str] = mapped_column(String(64), nullable=False)
    payload: Mapped[dict] = mapped_column(JSON, nullable=False)
    status: Mapped[str] = mapped_column(String(24), nullable=False)
    lease_epoch: Mapped[int | None] = mapped_column(Integer)
    claim_digest: Mapped[str | None] = mapped_column(String(64))
    deadline: Mapped[float | None] = mapped_column(Float)
    updated_at: Mapped[float] = mapped_column(Float, nullable=False)
    __table_args__ = (
        UniqueConstraint("mission_id", "task_key"),
        Index("ix_job_device_status", "vehicle_id", "status"),
        Index("ix_job_mission", "mission_id"),
    )


class RequestRecord(Base):
    __tablename__ = "idempotency"
    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    tenant: Mapped[str] = mapped_column(String(64), nullable=False)
    actor: Mapped[str] = mapped_column(String(128), nullable=False)
    key: Mapped[str] = mapped_column(String(36), nullable=False)
    operation: Mapped[str] = mapped_column(String(32), nullable=False)
    digest: Mapped[str] = mapped_column(String(64), nullable=False)
    resource: Mapped[str] = mapped_column(String(36), nullable=False)
    __table_args__ = (UniqueConstraint("tenant", "actor", "key"),)


class AckRecord(Base):
    __tablename__ = "job_ack_inbox"
    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    job_id: Mapped[str] = mapped_column(String(36), ForeignKey("jobs.id"), nullable=False)
    event_id: Mapped[str] = mapped_column(String(36), nullable=False)
    digest: Mapped[str] = mapped_column(String(64), nullable=False)
    phase: Mapped[str] = mapped_column(String(16), nullable=False)
    __table_args__ = (UniqueConstraint("job_id", "event_id"),)


class OutboxEvent(Base):
    __tablename__ = "event_outbox"
    id: Mapped[int] = mapped_column(Integer, primary_key=True, autoincrement=True)
    tenant: Mapped[str] = mapped_column(String(64), nullable=False)
    at: Mapped[float] = mapped_column(Float, nullable=False)
    event_type: Mapped[str] = mapped_column(String(64), nullable=False)
    aggregate_id: Mapped[str] = mapped_column(String(36), nullable=False)
    body: Mapped[dict] = mapped_column(JSON, nullable=False)
    __table_args__ = (Index("ix_outbox_tenant_sequence", "tenant", "id"),)
