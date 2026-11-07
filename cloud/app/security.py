"""OIDC JWT authorization. Do not trust user-supplied tenant or device headers."""
from __future__ import annotations

from dataclasses import dataclass
import re

import jwt
from fastapi import HTTPException
from jwt import PyJWKClient
from .config import Settings

IDENTIFIER = re.compile(r"^[A-Za-z0-9_-]{3,64}$")


@dataclass(frozen=True)
class Principal:
    subject: str
    tenant: str
    kind: str
    scopes: frozenset[str]
    vehicle_id: str | None = None

    def require(self, scope: str, kind: str = "operator") -> None:
        if self.kind != kind or scope not in self.scopes:
            raise HTTPException(403, "Insufficient authority")


class TokenVerifier:
    def __init__(self, settings: Settings):
        self.settings = settings
        self.jwks = (PyJWKClient(settings.jwks_url, cache_jwk_set=True,
                                 lifespan=300, timeout=3)
                     if settings.mode == "production" else None)

    def verify(self, token: str) -> Principal:
        try:
            if self.settings.mode == "production":
                key = self.jwks.get_signing_key_from_jwt(token).key
                algorithms = ["RS256"]
            else:
                key = self.settings.test_secret
                algorithms = ["HS256"]
            claims = jwt.decode(
                token, key, algorithms=algorithms,
                issuer=self.settings.issuer, audience=self.settings.audience,
                options={"require": ["exp", "iat", "nbf", "iss", "aud",
                                     "sub", "tenant", "kind", "scope"]},
                leeway=5,
            )
        except (jwt.PyJWTError, ValueError, TypeError, ConnectionError):
            raise HTTPException(401, "Invalid or expired credentials") from None
        tenant = claims.get("tenant")
        subject = claims.get("sub")
        kind = claims.get("kind")
        vehicle = claims.get("vehicle_id")
        scope = claims.get("scope")
        if (not isinstance(tenant, str) or not IDENTIFIER.fullmatch(tenant) or
                not isinstance(subject, str) or not 1 <= len(subject) <= 128 or
                kind not in ("operator", "device") or
                not isinstance(scope, str) or len(scope) > 512 or
                (kind == "device" and
                 (not isinstance(vehicle, str) or not IDENTIFIER.fullmatch(vehicle)))):
            raise HTTPException(401, "Malformed principal")
        return Principal(subject, tenant, kind,
                         frozenset(scope.split()), vehicle if kind == "device" else None)
