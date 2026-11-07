"""Durable expiry sweeper for virtual jobs; never dispatches or retries UAV work.

Run one or several replicas with PostgreSQL: row-level SKIP LOCKED prevents
duplicate expiry processing. A process crash cannot transform expired claims
into new commands. This process needs no public network listener.
"""
from __future__ import annotations

import logging
import os
import signal
import time

from sqlalchemy.orm import Session

from .main import create_app

LOG = logging.getLogger("aerohelm.cloud.scheduler")


def run_forever(*, interval_seconds: float = 1.0) -> None:
    if not .1 <= interval_seconds <= 30:
        raise ValueError("Unsafe scheduler interval")
    logging.basicConfig(level=logging.INFO)
    app = create_app()
    stop = False

    def stop_now(signum, frame):
        nonlocal stop
        stop = True

    signal.signal(signal.SIGTERM, stop_now)
    signal.signal(signal.SIGINT, stop_now)
    engine = app.state.engine
    while not stop:
        try:
            with Session(engine) as session:
                with session.begin():
                    app.state.sweep_expired_work(session)
        except Exception as exc:
            LOG.error("Expiration sweep failed: cause=%s", type(exc).__name__)
        time.sleep(interval_seconds)
    engine.dispose()


if __name__ == "__main__":
    run_forever()
