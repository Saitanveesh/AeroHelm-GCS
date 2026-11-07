"""Dedicated, bounded read-only cloud analytics process.

No HTTP listener, UART, remote commands, GCS DLLs, or per-frame UAV control.
Transactional PostgreSQL SKIP LOCKED allows multiple analytics replicas.
"""
from __future__ import annotations

import logging
import os
import signal
import time

from sqlalchemy import create_engine, func, select, text
from sqlalchemy.orm import Session

from .analytics import compute_one, prune_expired_reports
from .config import Settings
from .models import OutboxEvent

LOG = logging.getLogger("aerohelm.cloud.compute")


def run_forever(*, interval_seconds: float = 0.5) -> None:
    if not 0.1 <= interval_seconds <= 30:
        raise ValueError("Cloud compute poll interval out of range")
    cfg = Settings.from_env()
    logging.basicConfig(level=logging.INFO)
    engine = create_engine(cfg.database_url, pool_pre_ping=True, pool_recycle=1800)
    with engine.connect() as connection:
        version = connection.scalar(text("SELECT version_num FROM alembic_version"))
        if version != "0002_cloud_compute":
            raise RuntimeError("Cloud compute schema migration not applied")

    terminate = False

    def stop(signum, frame):
        nonlocal terminate
        terminate = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)

    def now(db: Session) -> float:
        if db.bind.dialect.name == "postgresql":
            return float(db.scalar(select(func.extract(
                "epoch", func.clock_timestamp()))))
        return time.time()  # test-only

    def audit(db: Session, tenant: str, typ: str, aggregate: str,
              actor: str) -> None:
        db.add(OutboxEvent(
            tenant=tenant, at=now(db), event_type=typ, aggregate_id=aggregate,
            body={"actor": actor, "mode": "READ_ONLY_ANALYTICS"}))

    last_prune = 0.0
    try:
        while not terminate:
            worked = False
            try:
                with Session(engine) as db:
                    with db.begin():
                        # Ephemeral analytics are retained for one day, while
                        # the append-only event outbox remains untouched.
                        if time.monotonic() - last_prune >= 60:
                            prune_expired_reports(db, now(db))
                            last_prune = time.monotonic()
                        worked = compute_one(db, now, audit)
            except Exception as exc:
                # Never log database URIs or sensitive payloads.
                LOG.error("Cloud compute transaction failed: cause=%s",
                          type(exc).__name__)
            if not worked:
                time.sleep(interval_seconds)
    finally:
        engine.dispose()


if __name__ == "__main__":
    run_forever()
