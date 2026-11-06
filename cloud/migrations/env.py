"""Cloud schema migrations; always provision before production app startup."""
from __future__ import annotations

import os

from alembic import context
from sqlalchemy import create_engine, pool

from cloud.app.models import Base

config = context.config
target_metadata = Base.metadata
url = os.environ.get("AEROHELM_DATABASE_URL", "")
if not url:
    raise RuntimeError("AEROHELM_DATABASE_URL is required for migration")


def run_migrations_offline():
    context.configure(url=url, target_metadata=target_metadata, literal_binds=True,
                      dialect_opts={"paramstyle": "named"})
    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online():
    engine = create_engine(url, poolclass=pool.NullPool)
    try:
        with engine.connect() as connection:
            context.configure(connection=connection, target_metadata=target_metadata)
            with context.begin_transaction():
                context.run_migrations()
    finally:
        engine.dispose()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
