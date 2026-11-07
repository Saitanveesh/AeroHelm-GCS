"""Durable simulation-only mission workflow.

There is deliberately NO flight-controller, MAVLink, cloud device, serial
or command transport dependency. This store cannot issue hardware commands.
"""
from __future__ import annotations

from contextlib import contextmanager
import hashlib
import hmac
import json
from pathlib import Path
import secrets
import sqlite3
import threading
import time
from typing import Callable, Iterator
import uuid

from .mission_preview import PreviewRequest, preview


class WorkflowError(ValueError):
    pass


class MissionStore:
    """SQLite simulator workflow, one exclusive virtual lease per aircraft."""

    def __init__(self, path: str | Path,
                 clock: Callable[[], float] = time.time):
        self.path = str(path)
        if self.path == ":memory:":
            raise ValueError("Use a persistent local test database path")
        self.clock = clock
        self._local_lock = threading.RLock()
        with self.transaction() as db:
            db.execute("""
                CREATE TABLE IF NOT EXISTS mission (
                    id TEXT PRIMARY KEY,
                    vehicle_id TEXT NOT NULL,
                    plan_sha256 TEXT NOT NULL,
                    plan_json TEXT NOT NULL,
                    state TEXT NOT NULL,
                    author TEXT NOT NULL,
                    approver TEXT,
                    lease_expires REAL,
                    lease_digest TEXT,
                    start_request_id TEXT,
                    next_index INTEGER NOT NULL DEFAULT 1,
                    created_at REAL NOT NULL
                )
            """)
            db.execute("""
                CREATE TABLE IF NOT EXISTS audit (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    mission_id TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    recorded_at REAL NOT NULL,
                    event_json TEXT NOT NULL,
                    previous_sha256 TEXT NOT NULL,
                    current_sha256 TEXT NOT NULL
                )
            """)

    @contextmanager
    def transaction(self) -> Iterator[sqlite3.Connection]:
        with self._local_lock:
            db = sqlite3.connect(self.path, timeout=4, isolation_level=None)
            try:
                db.row_factory = sqlite3.Row
                db.execute("PRAGMA busy_timeout=4000")
                db.execute("BEGIN IMMEDIATE")
                yield db
                db.commit()
            except Exception:
                db.rollback()
                raise
            finally:
                db.close()

    @staticmethod
    def _read(db: sqlite3.Connection, mission_id: str) -> sqlite3.Row:
        row = db.execute("SELECT * FROM mission WHERE id=?", (mission_id,)).fetchone()
        if row is None:
            raise WorkflowError("Mission does not exist")
        return row

    def _audit(self, db: sqlite3.Connection, mission_id: str,
               event_type: str, details: dict) -> None:
        row = db.execute(
            "SELECT current_sha256 FROM audit ORDER BY sequence DESC LIMIT 1"
        ).fetchone()
        previous = row["current_sha256"] if row else "0" * 64
        when = float(self.clock())
        payload = json.dumps({"mission": mission_id, "event": event_type,
                              "time": when, "details": details},
                             sort_keys=True, separators=(",", ":"))
        digest = hashlib.sha256((previous + payload).encode()).hexdigest()
        db.execute("""
            INSERT INTO audit(mission_id,event_type,recorded_at,event_json,
                              previous_sha256,current_sha256)
            VALUES (?,?,?,?,?,?)
        """, (mission_id, event_type, when, payload, previous, digest))

    def _expire(self, db: sqlite3.Connection) -> None:
        expired = db.execute("""
            SELECT id FROM mission
            WHERE state='RUNNING_SIM' AND lease_expires <= ?
        """, (float(self.clock()),)).fetchall()
        for row in expired:
            db.execute("""
                UPDATE mission SET state='EXPIRED_SIM',lease_digest=NULL,
                                   lease_expires=NULL WHERE id=?
            """, (row["id"],))
            self._audit(db, row["id"], "LEASE_EXPIRED_SIM", {})

    def create(self, request: PreviewRequest, author: str) -> dict:
        if not author or len(author) > 128:
            raise WorkflowError("An identified simulation author is required")
        result = preview(request)
        mission_id = uuid.uuid4().hex
        with self.transaction() as db:
            db.execute("""
                INSERT INTO mission (id,vehicle_id,plan_sha256,plan_json,
                                     state,author,created_at)
                VALUES (?,?,?,?,?,?,?)
            """, (mission_id, request.vehicle_id, result["revision_sha256"],
                  json.dumps(result, sort_keys=True), "DRAFT_SIM",
                  author, float(self.clock())))
            self._audit(db, mission_id, "DRAFT_CREATED_SIM",
                        {"sha256": result["revision_sha256"], "author": author})
        return {"mission_id": mission_id, "state": "DRAFT_SIM",
                "revision_sha256": result["revision_sha256"]}

    def approve(self, mission_id: str, approver: str,
                expected_sha256: str) -> dict:
        if not approver or len(approver) > 128:
            raise WorkflowError("Identified simulation approver required")
        with self.transaction() as db:
            self._expire(db)
            row = self._read(db, mission_id)
            if row["state"] != "DRAFT_SIM":
                raise WorkflowError("Only a draft can be approved")
            if hmac.compare_digest(row["author"], approver):
                raise WorkflowError("Author and approver must be different people")
            if not hmac.compare_digest(row["plan_sha256"], expected_sha256):
                raise WorkflowError("Mission revision does not match approval")
            db.execute("UPDATE mission SET state='APPROVED_SIM',approver=? WHERE id=?",
                       (approver, mission_id))
            self._audit(db, mission_id, "APPROVED_SIM",
                        {"approver": approver, "sha256": expected_sha256})
            return {"mission_id": mission_id, "state": "APPROVED_SIM"}

    def start(self, mission_id: str, expected_sha256: str,
              request_id: str, lease_seconds: int = 30) -> dict:
        if not 10 <= lease_seconds <= 120 or len(request_id) < 16:
            raise WorkflowError("Invalid simulation lease or idempotency identifier")
        with self.transaction() as db:
            self._expire(db)
            row = self._read(db, mission_id)
            if row["state"] != "APPROVED_SIM":
                raise WorkflowError("Mission is not approved or was already started")
            if not hmac.compare_digest(row["plan_sha256"], expected_sha256):
                raise WorkflowError("Mission revision changed after approval")
            other = db.execute("""
                SELECT id FROM mission
                WHERE vehicle_id=? AND state='RUNNING_SIM' AND id!=?
            """, (row["vehicle_id"], mission_id)).fetchone()
            if other:
                raise WorkflowError("A simulation lease already owns this vehicle")
            token = secrets.token_urlsafe(36)
            digest = hashlib.sha256(token.encode()).hexdigest()
            expiry = float(self.clock()) + lease_seconds
            db.execute("""
                UPDATE mission SET state='RUNNING_SIM',lease_digest=?,
                    lease_expires=?,start_request_id=? WHERE id=?
            """, (digest, expiry, request_id, mission_id))
            self._audit(db, mission_id, "STARTED_SIM",
                        {"request_id": request_id, "expires": expiry})
            return {"mission_id": mission_id, "state": "RUNNING_SIM",
                    "lease_token": token, "expires_at": expiry}

    def step(self, mission_id: str, lease_token: str,
             expected_index: int) -> dict:
        if not lease_token:
            raise WorkflowError("Simulation lease token required")
        with self.transaction() as db:
            self._expire(db)
            row = self._read(db, mission_id)
            if row["state"] != "RUNNING_SIM":
                raise WorkflowError("Simulation is not active")
            if not hmac.compare_digest(
                row["lease_digest"], hashlib.sha256(lease_token.encode()).hexdigest()
            ):
                raise WorkflowError("Invalid simulation lease token")
            if expected_index != row["next_index"]:
                raise WorkflowError("Out-of-order or duplicate virtual waypoint")
            plan = json.loads(row["plan_json"])
            if expected_index <= 0 or expected_index >= len(plan["timeline"]):
                raise WorkflowError("Virtual waypoint index outside mission plan")
            complete = expected_index == len(plan["timeline"]) - 1
            state = "COMPLETED_SIM" if complete else "RUNNING_SIM"
            db.execute("""
                UPDATE mission SET next_index=?,state=?,
                    lease_digest=?,lease_expires=? WHERE id=?
            """, (expected_index + 1, state,
                  None if complete else row["lease_digest"],
                  None if complete else row["lease_expires"], mission_id))
            self._audit(db, mission_id, "WAYPOINT_SIM",
                        {"index": expected_index, "complete": complete})
            return {"mission_id": mission_id, "state": state,
                    "virtual_position": plan["timeline"][expected_index],
                    "no_aircraft_command": True}

    def abort(self, mission_id: str, actor: str) -> dict:
        with self.transaction() as db:
            self._expire(db)
            row = self._read(db, mission_id)
            if actor not in (row["author"], row["approver"]):
                raise WorkflowError("Unknown simulation participant")
            if row["state"] not in ("DRAFT_SIM", "APPROVED_SIM", "RUNNING_SIM"):
                raise WorkflowError("Simulation mission is terminal")
            db.execute("""
                UPDATE mission SET state='ABORTED_SIM',
                    lease_digest=NULL,lease_expires=NULL WHERE id=?
            """, (mission_id,))
            self._audit(db, mission_id, "ABORTED_SIM", {"actor": actor})
            return {"mission_id": mission_id, "state": "ABORTED_SIM",
                    "no_aircraft_command": True}

    def status(self, mission_id: str) -> dict:
        with self.transaction() as db:
            self._expire(db)
            row = self._read(db, mission_id)
            return {"mission_id": mission_id, "state": row["state"],
                    "revision_sha256": row["plan_sha256"],
                    "next_virtual_waypoint": row["next_index"],
                    "vehicle_command_authorized": False}

    def audit_valid(self) -> bool:
        """Detect accidental local audit corruption; not WORM evidence."""
        with self.transaction() as db:
            previous = "0" * 64
            for row in db.execute("SELECT * FROM audit ORDER BY sequence"):
                payload = row["event_json"]
                expected = hashlib.sha256((previous + payload).encode()).hexdigest()
                if not hmac.compare_digest(row["previous_sha256"], previous) or not \
                   hmac.compare_digest(row["current_sha256"], expected):
                    return False
                previous = expected
            return True
