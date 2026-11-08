"""Fail-closed OCI IMDSv2 compute-instance binding for the Vihanga relay.

An OCID is a cloud-resource identity, NOT a transport address or a credential.
The expected OCID must come from secure deployment configuration; metadata
is read only from the fixed link-local Oracle IMDSv2 endpoint.
"""
from __future__ import annotations

import hmac
import json
import re
from urllib.request import Request, urlopen

METADATA_URL = "http://169.254.169.254/opc/v2/instance/"
OCID = re.compile(r"^ocid1\.instance\.oc[0-9]+\.(?:[A-Za-z0-9_-]*\.)*[A-Za-z0-9_-]{12,}$")


def validate_instance_ocid(expected: str) -> None:
    if not isinstance(expected, str) or not OCID.fullmatch(expected):
        raise ValueError("Expected a full Oracle Compute instance OCID, not a display name or hostname")


def verify_instance_binding(expected: str, fetch=None) -> dict:
    """Check actual local Oracle instance; startup fails on mismatch/outage.

    A private HTTPS hostname is independently verified by TLS/Tailscale;
    matching an OCID does not authenticate an arbitrary URL.
    """
    if not expected:
        return {"configured": False, "verified": False}
    validate_instance_ocid(expected)
    if fetch is None:
        def fetch():
            request = Request(
                METADATA_URL, headers={"Authorization": "Bearer Oracle"})
            with urlopen(request, timeout=3.0) as response:
                if response.geturl() != METADATA_URL:
                    raise RuntimeError("OCI metadata redirect is not allowed")
                data = response.read(16385)
                if len(data) > 16384:
                    raise RuntimeError("OCI metadata response too large")
                return json.loads(data)
    try:
        metadata = fetch()
    except Exception as ex:
        raise RuntimeError("Cannot verify local OCI compute instance using IMDSv2") from ex
    actual = metadata.get("id") if isinstance(metadata, dict) else None
    if not isinstance(actual, str) or not hmac.compare_digest(actual, expected):
        raise RuntimeError("OCI compute instance identity does not match deployment")
    return {"configured": True, "verified": True}
