"""Add isolated cloud analytics queue, after immutable initial schema.

Revision ID: 0002_cloud_compute
Revises: 0001_initial
"""
from alembic import op

from cloud.app.models import CloudComputeJob

revision = "0002_cloud_compute"
down_revision = "0001_initial"
branch_labels = None
depends_on = None


def upgrade():
    CloudComputeJob.__table__.create(bind=op.get_bind(), checkfirst=True)


def downgrade():
    # Preserve job receipts/audit; destructive downgrades require a reviewed
    # retention and recovery procedure, never automatic rollback.
    raise RuntimeError("Destructive cloud analytics downgrade prohibited")
