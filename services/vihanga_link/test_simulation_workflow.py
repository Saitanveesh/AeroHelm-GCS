"""Durable simulator-only state-machine tests, independent of any UAV."""
from __future__ import annotations

import pytest

from services.vihanga_link.mission_preview import PreviewRequest
from services.vihanga_link.simulation_workflow import MissionStore, WorkflowError
from services.vihanga_link.test_mission_preview import fixture_request


def test_two_person_approval_and_ordered_completion(tmp_path):
    clock = [1000.0]
    store = MissionStore(tmp_path / "missions.sqlite3", clock=lambda: clock[0])
    mission = PreviewRequest.model_validate(fixture_request())
    draft = store.create(mission, "planner-A")
    assert store.status(draft["mission_id"])["state"] == "DRAFT_SIM"
    with pytest.raises(WorkflowError, match="different people"):
        store.approve(draft["mission_id"], "planner-A", draft["revision_sha256"])
    with pytest.raises(WorkflowError, match="revision"):
        store.approve(draft["mission_id"], "reviewer-B", "0" * 64)
    approved = store.approve(draft["mission_id"], "reviewer-B",
                             draft["revision_sha256"])
    assert approved["state"] == "APPROVED_SIM"
    started = store.start(draft["mission_id"], draft["revision_sha256"],
                          "idempotency-request-0001", lease_seconds=30)
    assert started["state"] == "RUNNING_SIM"
    with pytest.raises(WorkflowError, match="already started"):
        store.start(draft["mission_id"], draft["revision_sha256"],
                    "idempotency-request-0001", lease_seconds=30)
    with pytest.raises(WorkflowError, match="Invalid simulation lease"):
        store.step(draft["mission_id"], "wrong-token", 1)
    with pytest.raises(WorkflowError, match="Out-of-order"):
        store.step(draft["mission_id"], started["lease_token"], 2)
    first = store.step(draft["mission_id"], started["lease_token"], 1)
    assert first["no_aircraft_command"] and first["state"] == "RUNNING_SIM"
    last = store.step(draft["mission_id"], started["lease_token"], 2)
    assert last["no_aircraft_command"] and last["state"] == "COMPLETED_SIM"
    with pytest.raises(WorkflowError, match="not active"):
        store.step(draft["mission_id"], started["lease_token"], 2)
    assert store.audit_valid()
    # Persistence over new SQLite connections.
    second = MissionStore(tmp_path / "missions.sqlite3", clock=lambda: clock[0])
    assert second.status(draft["mission_id"])["state"] == "COMPLETED_SIM"
    assert second.audit_valid()


def test_exclusive_vehicle_lease_and_expiry(tmp_path):
    current = [50.0]
    store = MissionStore(tmp_path / "lease.sqlite3", clock=lambda: current[0])
    plan = PreviewRequest.model_validate(fixture_request())
    def ready(author, reviewer):
        row = store.create(plan, author)
        store.approve(row["mission_id"], reviewer, row["revision_sha256"])
        return row
    a = ready("operator-A", "operator-B")
    b = ready("operator-C", "operator-D")
    token = store.start(a["mission_id"], a["revision_sha256"],
                        "start-request-alpha", lease_seconds=10)
    with pytest.raises(WorkflowError, match="already owns"):
        store.start(b["mission_id"], b["revision_sha256"],
                    "start-request-bravo", lease_seconds=10)
    current[0] = 61.0
    assert store.status(a["mission_id"])["state"] == "EXPIRED_SIM"
    with pytest.raises(WorkflowError, match="not active"):
        store.step(a["mission_id"], token["lease_token"], 1)
    assert store.start(b["mission_id"], b["revision_sha256"],
                       "start-request-charlie", lease_seconds=10)["state"] == "RUNNING_SIM"
    assert store.audit_valid()


def test_abort_is_terminal_and_requires_participant(tmp_path):
    store = MissionStore(tmp_path / "abort.sqlite3")
    plan = PreviewRequest.model_validate(fixture_request())
    draft = store.create(plan, "planner")
    with pytest.raises(WorkflowError, match="Unknown simulation participant"):
        store.abort(draft["mission_id"], "stranger")
    assert store.abort(draft["mission_id"], "planner")["state"] == "ABORTED_SIM"
    with pytest.raises(WorkflowError, match="terminal"):
        store.abort(draft["mission_id"], "planner")
    with pytest.raises(WorkflowError, match="Only a draft"):
        store.approve(draft["mission_id"], "reviewer", draft["revision_sha256"])


def test_audit_chain_detects_corruption(tmp_path):
    store = MissionStore(tmp_path / "tamper.sqlite3")
    row = store.create(PreviewRequest.model_validate(fixture_request()), "planner")
    assert store.audit_valid()
    with store.transaction() as db:
        db.execute("UPDATE audit SET event_json='tampered' WHERE mission_id=?",
                   (row["mission_id"],))
    assert not store.audit_valid()
