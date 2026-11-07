"""Adversarial token and principal isolation tests; no external identity provider."""
from __future__ import annotations

import time

import jwt
from fastapi.testclient import TestClient

from cloud.app.main import create_app
from cloud.tests.test_control_plane import TEST_SECRET, config, jwt_header


def malformed(**kwargs):
    t = int(time.time())
    payload = {
        "iss": "aerohelm-cloud-test",
        "aud": "aerohelm-cloud", "sub": "pilot",
        "tenant": "TENANT-A", "kind": "operator",
        "scope": "fleet:read", "iat": t, "nbf": t, "exp": t+60,
    }
    payload.update(kwargs)
    return {"Authorization":"Bearer " + jwt.encode(
        payload, TEST_SECRET, algorithm="HS256")}


def test_unsigned_wrong_issuer_expired_and_wrong_scope_rejected():
    with TestClient(create_app(config())) as client:
        path = "/api/v1/capabilities"
        assert client.get(path).status_code == 401
        assert client.get(path, headers=malformed(iss="attacker")).status_code == 401
        assert client.get(path, headers=malformed(aud="wrong-target")).status_code == 401
        assert client.get(path, headers=malformed(exp=int(time.time()) - 60)).status_code == 401
        assert client.get(path, headers=malformed(tenant="bad/tenant")).status_code == 401
        assert client.get(path, headers=malformed(scope="mission:write")).status_code == 403
        assert client.get(path, headers=jwt_header()).status_code == 200


def test_unsigned_jwt_never_grants_a_role():
    with TestClient(create_app(config())) as client:
        t = int(time.time())
        token = jwt.encode({
            "iss":"aerohelm-cloud-test","aud":"aerohelm-cloud",
            "sub":"attacker","tenant":"TENANT-A","kind":"operator",
            "scope":"fleet:admin fleet:read","iat":t,"nbf":t,"exp":t+300,
        }, key="", algorithm="none")
        assert client.get("/api/v1/vehicles",
                          headers={"Authorization":"Bearer "+token}).status_code == 401


def test_device_cannot_claim_operator_or_own_other_tenant():
    with TestClient(create_app(config())) as client:
        device = jwt_header("device-one", kind="device", device="SIM-A",
                            scopes="jobs:claim jobs:ack fleet:admin fleet:read")
        assert client.get("/api/v1/vehicles", headers=device).status_code == 403
        assert client.post("/api/v1/vehicles",
                           headers=device,json={
                               "vehicle_id":"SIM-A","kind":"virtual",
                               "profile":{"home_lat":12.97,"home_lon":77.59,
                                          "energy_wh":100,"reserve_fraction":.35,
                                          "energy_wh_per_km":20}
                           }).status_code == 403


def test_production_fails_closed_without_explicit_trusted_host(monkeypatch):
    import pytest
    from cloud.app.config import Settings
    production = Settings(
        mode="production", database_url="postgresql+psycopg://ci:ci@localhost/ci",
        issuer="https://issuer.example.invalid", audience="aerohelm-cloud",
        jwks_url="https://issuer.example.invalid/jwks", test_secret=None,
    )
    monkeypatch.delenv("AEROHELM_ALLOWED_HOSTS", raising=False)
    with pytest.raises(ValueError, match="trusted hosts"):
        create_app(production)
    monkeypatch.setenv("AEROHELM_ALLOWED_HOSTS", "*")
    with pytest.raises(ValueError, match="trusted hosts"):
        create_app(production)
    monkeypatch.setenv("AEROHELM_ALLOWED_HOSTS", "aerohelm-private.example.invalid")
    app = create_app(production)
    assert app.title == "AeroHelm Cloud Fleet Control Plane"
