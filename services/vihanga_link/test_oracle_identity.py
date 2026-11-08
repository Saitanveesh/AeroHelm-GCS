"""OCI metadata binding tests. Never contact OCI in this offline suite."""
from __future__ import annotations

import pytest
import hashlib

from services.vihanga_link.oracle_identity import (
    METADATA_URL, validate_instance_ocid, verify_instance_binding,
)

EXPECTED = "ocid1.instance.oc1.ap-hyderabad-1." + "a" * 48
OTHER = "ocid1.instance.oc1.ap-hyderabad-1." + "b" * 48


def test_oracle_instance_identified_from_local_metadata_only():
    validate_instance_ocid(EXPECTED)
    calls = []
    def fake_metadata():
        calls.append(METADATA_URL)
        return {"id": EXPECTED, "displayName": "new-m1", "region": "ap-hyderabad-1"}
    result = verify_instance_binding(EXPECTED, fetch=fake_metadata)
    assert calls == [METADATA_URL]
    assert result == {"configured": True, "verified": True,
                      "instance_sha256": hashlib.sha256(EXPECTED.encode()).hexdigest()}
    assert EXPECTED not in str(result)
    assert verify_instance_binding("", fetch=lambda: pytest.fail("unexpected request")) == {
        "configured": False, "verified": False
    }


@pytest.mark.parametrize("bad", [
    "old-m1", "https://server.ts.net", "ocid1.vcn.oc1..aaa",
    "ocid1.instance.oc1..", "ocid1.instance.oc1..a" * 2, "",
])
def test_bad_compute_ocid_rejected(bad):
    with pytest.raises(ValueError):
        validate_instance_ocid(bad)


def test_wrong_instance_or_unreachable_metadata_blocks_start():
    with pytest.raises(RuntimeError, match="does not match"):
        verify_instance_binding(EXPECTED, fetch=lambda: {"id": OTHER})
    with pytest.raises(RuntimeError, match="does not match"):
        verify_instance_binding(EXPECTED, fetch=lambda: {"displayName": "new-m1"})
    def unavailable():
        raise TimeoutError("offline")
    with pytest.raises(RuntimeError, match="IMDSv2"):
        verify_instance_binding(EXPECTED, fetch=unavailable)
