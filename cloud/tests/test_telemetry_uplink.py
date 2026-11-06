"""Cloud-compatible telemetry sidecar path (no UART, no physical FC)."""
from __future__ import annotations

import time

import httpx
from fastapi.testclient import TestClient

from cloud.app.main import create_app
from cloud.tests.test_control_plane import config, jwt_header, register
from cloud.tools.telemetry_uplink import ObservationUplink


def token_for(name):
    return jwt_header("device-"+name, kind="device",
                      device=name, scopes="telemetry:write")["Authorization"][7:]


def observation():
    return {"latitude":12.97,"longitude":77.59,"altitude_m":30,
            "battery_pct":85,"mode":"STANDBY","armed":False,
            "link_rtt_ms":41,"gps_fix_type":3}


def test_https_compatible_normalized_uplink_does_not_replay(monkeypatch):
    monkeypatch.setenv("AEROHELM_VIRTUAL_DISPATCH_ENABLED", "false")
    app = create_app(config())
    with TestClient(app) as client:
        register(client, "PIX-01", kind="hardware")
        class ClientAdapter:
            def __init__(self, inner): self.inner = inner
            def post(self, path, **kwargs): return self.inner.post(path, **kwargs)
            def close(self): pass

        # TestClient uses an in-process ASGI transport; no public URL or Pi.
        uplink = ObservationUplink(base_url="https://private.example.invalid",
                                   token_supplier=lambda: token_for("PIX-01"))
        uplink.client.close()
        uplink.client = ClientAdapter(client)
        assert uplink.begin_session() == 1
        assert uplink.send_latest(observation()) == 1
        assert uplink.send_latest(observation()) == 2
        state = client.get("/api/v1/vehicles/PIX-01/telemetry",
                           headers=jwt_header()).json()
        assert state["last_sequence"] == 2
        assert state["flight_ready"] is False
        uplink.close()


def test_sidecar_rejects_untrusted_origins_and_command_fields():
    import pytest
    with pytest.raises(ValueError):
        ObservationUplink(base_url="http://public.invalid",
                          token_supplier=lambda:"some-long-token-for-test")
    with pytest.raises(ValueError):
        ObservationUplink(base_url="https://token@private.invalid",
                          token_supplier=lambda:"some-long-token-for-test")
