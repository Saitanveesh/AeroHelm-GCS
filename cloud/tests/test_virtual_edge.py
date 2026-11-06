"""Full HTTP request/claim/ACK path with two independent virtual vehicles."""
from __future__ import annotations

import uuid

from fastapi.testclient import TestClient

from cloud.app.main import create_app
from cloud.tools.sim_edge import VirtualEdge
from cloud.tests.test_control_plane import (
    approve, config, draft, jwt_header, lease, login, make_two, spec,
)

# Aliases of the already tested signed token fixture, no live credentials.
from cloud.tests.test_control_plane import acquire, register, start


def test_two_virtual_devices_complete_multi_uav_workflow(monkeypatch):
    monkeypatch.setenv("AEROHELM_VIRTUAL_DISPATCH_ENABLED", "true")
    with TestClient(create_app(config())) as client:
        register(client, "SIM-A", 12.97)
        register(client, "SIM-B", 12.99)
        mission = draft(client).json()
        assert approve(client, mission).status_code == 200
        assert acquire(client, "SIM-A").status_code == 200
        assert acquire(client, "SIM-B").status_code == 200
        assert start(client, mission).status_code == 202
        from cloud.tests.test_control_plane import jwt_header, TEST_SECRET
        # The device never receives an operator JWT or a MAVLink stream.
        import jwt
        import time
        now = int(time.time())

        def token(name):
            return jwt.encode({
                "iss": "aerohelm-cloud-test", "aud": "aerohelm-cloud",
                "sub": "edge-" + name, "tenant": "TENANT-A", "kind": "device",
                "vehicle_id": name, "scope": "jobs:claim jobs:ack",
                "iat": now, "nbf": now, "exp": now + 300,
            }, TEST_SECRET, algorithm="HS256")

        for name in ("SIM-A", "SIM-B"):
            edge = VirtualEdge(client, device_id=name, bearer=token(name))
            first = edge.poll_once()
            assert first["status"] == "COMPLETED_SIM"
            assert first["flight_was_performed"] is False
            assert edge.poll_once() == {"status": "IDLE_SIM"}

        result = client.get("/api/v1/missions/" + mission["id"],
                            headers=jwt_header()).json()
        assert result["status"] == "COMPLETED_SIM"


def test_virtual_adapter_refuses_hardware_identity():
    import pytest
    with pytest.raises(ValueError):
        VirtualEdge(None, device_id="PIXHAWK-REAL", bearer="nope")
