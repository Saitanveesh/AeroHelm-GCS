"""Cloud schema migrations; always provision before production app startup."""
from __future__ import annotations

import os

from alembic import context
from sqlalchemy import create_engine, pool

from cloud.app.models import Base

config = context.config
target_metadata = Base.metadata
url = os.environ.get("AEROHELM_DATABASE_URL", "")
url_file = os.environ.get("AEROHELM_DATABASE_URL_FILE", "")
if url and url_file:
    raise RuntimeError("Supply one database URL source, not both")
if url_file:
    from pathlib import Path
    url = Path(url_file).read_text(encoding="utf8").strip()
if not url:
    raise RuntimeError("Database URL or secret file is required for migration")


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
