"""Initial durable fleet, mission, lease, inbox and outbox schema.

Revision ID: 0001_initial
Revises:
Create Date: 2026-11-06
"""
from alembic import op
from cloud.app.models import Base

revision = "0001_initial"
down_revision = None
branch_labels = None
depends_on = None


def upgrade():
    # An initial schema revision must remain immutable as models evolve.
    # New tables belong to later revisions; never create future models here.
    initial_tables = (
        "vehicles", "control_leases", "missions", "jobs",
        "idempotency", "job_ack_inbox", "event_outbox", "telemetry_latest",
    )
    Base.metadata.create_all(
        bind=op.get_bind(),
        tables=[Base.metadata.tables[name] for name in initial_tables],
        checkfirst=True,
    )


def downgrade():
    # Requiring manual disaster-recovery review prevents an accidental
    # destructive rollback of live mission authority or audit evidence.
    raise RuntimeError("Destructive cloud schema downgrade is prohibited")
