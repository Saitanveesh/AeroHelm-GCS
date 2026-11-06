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
    Base.metadata.create_all(bind=op.get_bind(), checkfirst=True)


def downgrade():
    # Requiring manual disaster-recovery review prevents an accidental
    # destructive rollback of live mission authority or audit evidence.
    raise RuntimeError("Destructive cloud schema downgrade is prohibited")
