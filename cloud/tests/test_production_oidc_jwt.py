"""Production-algorithm OIDC verification without contacting external IdP."""
from __future__ import annotations

import time

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa
from fastapi import HTTPException

from cloud.app.config import Settings
from cloud.app.security import TokenVerifier


def prod_verifier():
    private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    settings = Settings(
        mode="production",
        database_url="postgresql+psycopg://dummy:dummy@localhost/aerohelm_ci",
        issuer="https://idp.example.invalid/realms/aerohelm",
        audience="aerohelm-cloud",
        jwks_url="https://idp.example.invalid/realms/aerohelm/certs",
        test_secret=None,
    )
    verifier = TokenVerifier(settings)

    class StubSigningKey:
        key = private.public_key()

    class StubJwks:
        def get_signing_key_from_jwt(self, _):
            return StubSigningKey()

    verifier.jwks = StubJwks()
    return verifier, private


def claims(**overrides):
    t = int(time.time())
    values = {
        "iss": "https://idp.example.invalid/realms/aerohelm",
        "aud": "aerohelm-cloud", "sub": "edge-PIX-01",
        "tenant": "TENANT-A", "kind": "device",
        "vehicle_id": "PIX-01", "scope": "telemetry:write",
        "iat": t, "nbf": t, "exp": t + 60,
    }
    values.update(overrides)
    return values


def test_real_rs256_signature_and_device_claims():
    verifier, private = prod_verifier()
    token = jwt.encode(claims(), private, algorithm="RS256")
    principal = verifier.verify(token)
    assert principal.kind == "device"
    assert principal.tenant == "TENANT-A"
    assert principal.vehicle_id == "PIX-01"
    assert principal.scopes == frozenset(("telemetry:write",))
    with pytest.raises(HTTPException) as forbidden:
        principal.require("mission:execute")
    assert forbidden.value.status_code == 403


@pytest.mark.parametrize("override", [
    {"iss": "https://attacker.invalid"},
    {"aud": "unrelated"},
    {"exp": 1},
    {"tenant": "other/tenant"},
    {"vehicle_id": "bad/serial"},
    {"scope": 4},
    {"kind": "operator", "tenant": "bad/tenant"},
])
def test_production_bad_claims_fail_closed(override):
    verifier, private = prod_verifier()
    token = jwt.encode(claims(**override), private, algorithm="RS256")
    with pytest.raises(HTTPException) as exc:
        verifier.verify(token)
    assert exc.value.status_code == 401


def test_hs256_cannot_impersonate_rs256_device():
    verifier, _ = prod_verifier()
    token = jwt.encode(claims(), "not-a-real-jwk", algorithm="HS256")
    with pytest.raises(HTTPException) as exc:
        verifier.verify(token)
    assert exc.value.status_code == 401
