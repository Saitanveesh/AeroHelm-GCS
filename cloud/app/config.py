"""Fail-closed cloud settings. Test signing is impossible outside test mode."""
from __future__ import annotations

import os
from dataclasses import dataclass
from urllib.parse import urlsplit


@dataclass(frozen=True)
class Settings:
    mode: str
    database_url: str
    issuer: str
    audience: str
    jwks_url: str | None
    test_secret: str | None

    @classmethod
    def from_env(cls) -> "Settings":
        mode = os.getenv("AEROHELM_CLOUD_MODE", "production").strip().lower()
        if mode not in {"production", "test"}:
            raise ValueError("Invalid AEROHELM_CLOUD_MODE")
        db = os.getenv("AEROHELM_DATABASE_URL", "")
        secret_file = os.getenv("AEROHELM_DATABASE_URL_FILE", "")
        if db and secret_file:
            raise ValueError("Specify one database URL source, never both")
        if secret_file:
            from pathlib import Path
            db = Path(secret_file).read_text(encoding="utf8").strip()
        if not db:
            raise ValueError("AEROHELM_DATABASE_URL is required")
        if mode == "production" and not db.startswith("postgresql+psycopg://"):
            raise ValueError("Production requires PostgreSQL with psycopg")
        issuer = os.getenv("AEROHELM_OIDC_ISSUER", "")
        audience = os.getenv("AEROHELM_OIDC_AUDIENCE", "aerohelm-cloud")
        jwks = os.getenv("AEROHELM_OIDC_JWKS_URL", "")
        secret = os.getenv("AEROHELM_TEST_JWT_SECRET")
        if mode == "production":
            if secret:
                raise ValueError("Test signing secret forbidden in production")
            for value in (issuer, jwks):
                parts = urlsplit(value)
                if parts.scheme != "https" or not parts.hostname or parts.username or parts.password or parts.fragment:
                    raise ValueError("Issuer and JWKS must use trusted HTTPS URLs")
            if not audience or len(audience) > 128:
                raise ValueError("OIDC audience invalid")
        else:
            issuer = issuer or "aerohelm-cloud-test"
            if not secret or len(secret) < 32:
                raise ValueError("Test mode requires a 32+ character test secret")
        return cls(mode, db, issuer, audience, jwks or None, secret)
