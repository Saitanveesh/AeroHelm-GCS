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


def test_ambiguous_delivery_invalidates_session_instead_of_replaying():
    import pytest
    app = create_app(config())
    with TestClient(app) as client:
        assert register(client, "SIM-A").status_code == 201

        class DropsResponse:
            def __init__(self, actual):
                self.actual = actual
                self.once = True

            def post(self, path, **kwargs):
                delivered = self.actual.post(path, **kwargs)
                if self.once and path == "/api/v1/device/telemetry":
                    self.once = False
                    # Server committed, but caller never received its receipt.
                    raise httpx.ReadTimeout("response lost")
                return delivered

            def close(self):
                pass

        uplink = ObservationUplink(
            base_url="https://private.example.invalid",
            token_supplier=lambda: token_for("SIM-A"),
        )
        uplink.client.close()
        uplink.client = DropsResponse(client)
        first_epoch = uplink.begin_session()
        with pytest.raises(httpx.ReadTimeout):
            uplink.send_latest(observation())
        assert uplink.epoch is None
        with pytest.raises(ValueError, match="No established"):
            uplink.send_latest(observation())
        next_epoch = uplink.begin_session()
        assert next_epoch > first_epoch
        assert uplink.send_latest(observation()) == 1
        state = client.get("/api/v1/vehicles/SIM-A/telemetry",
                           headers=jwt_header()).json()
        assert state["session_epoch"] == next_epoch
        assert state["last_sequence"] == 1
        uplink.close()
