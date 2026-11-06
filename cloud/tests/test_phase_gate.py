"""Release gate must reject zero CI, wrong SHA, failures and pending runs."""
import pytest

from cloud.tools.phase_gate import WORKFLOW, verified_ci


def sha(ch="a"):
    return ch * 40


def test_zero_workflow_runs_fail_closed():
    assert verified_ci(sha(), fetcher=lambda url: {
        "total_count": 0, "workflow_runs": [],
    }) is False


@pytest.mark.parametrize("status,conclusion,commit,name", [
    ("queued", None, sha(), WORKFLOW),
    ("in_progress", None, sha(), WORKFLOW),
    ("completed", "failure", sha(), WORKFLOW),
    ("completed", "cancelled", sha(), WORKFLOW),
    ("completed", "success", sha("b"), WORKFLOW),
    ("completed", "success", sha(), "Unrelated workflow"),
])
def test_mismatched_or_failed_evidence_cannot_promote(status, conclusion, commit, name):
    assert not verified_ci(sha(), fetcher=lambda _: {
        "workflow_runs": [{
            "head_sha": commit, "name": name,
            "status": status, "conclusion": conclusion,
        }],
    })


def test_only_exact_completed_cloud_workflow_passes():
    assert verified_ci(sha(), fetcher=lambda _: {
        "workflow_runs": [{
            "head_sha": sha(), "name": WORKFLOW,
            "status": "completed", "conclusion": "success",
        }],
    })


def test_invalid_commit_sha_rejected_before_network():
    with pytest.raises(ValueError):
        verified_ci("main", fetcher=lambda _: {
            "workflow_runs": [],
        })
