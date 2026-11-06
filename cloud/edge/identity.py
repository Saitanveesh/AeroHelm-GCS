"""Private OIDC client-credentials identity for the Pi observation sidecar.

Uses ONLY Python stdlib and validated HTTPS. The issuer must mint an RS256
device JWT with tenant/kind=device/vehicle_id/telemetry:write claims; this
client cannot forge those claims. Client secret stays in a restricted file.
"""
from __future__ import annotations

import json
import os
import re
import stat
import ssl
import time
from urllib import error, parse, request


class DenyRedirect(request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def validated_https_url(value: str, *, permit_path: bool = True) -> str:
    if not isinstance(value, str) or value != value.strip():
        raise ValueError("Cloud/OIDC URL must be a clean HTTPS URL")
    uri = parse.urlsplit(value)
    try:
        port = uri.port  # force validation of malformed port strings
    except ValueError:
        raise ValueError("Invalid HTTPS port") from None
    if (uri.scheme != "https" or not uri.hostname or uri.username or
            uri.password or uri.query or uri.fragment or
            (not permit_path and uri.path not in ("", "/")) or
            not uri.netloc or "\\" in value or
            not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.-]*", uri.hostname) or
            (port is not None and not 1 <= port <= 65535)):
        raise ValueError("Only credential-free HTTPS origins are allowed")
    return value


def secure_opener(cafile: str | None = None):
    context = ssl.create_default_context(cafile=cafile)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    return request.build_opener(request.HTTPSHandler(context=context),
                                DenyRedirect())


class DeviceTokenProvider:
    def __init__(self, *, token_endpoint: str, client_id: str,
                 client_secret_file: str, cafile: str | None = None,
                 opener=None, clock=time.monotonic):
        self.token_endpoint = validated_https_url(token_endpoint)
        if not isinstance(client_id, str) or not 3 <= len(client_id) <= 128:
            raise ValueError("OIDC client ID must be configured")
        if not os.path.isabs(client_secret_file):
            raise ValueError("OIDC client secret must be an absolute file path")
        self.client_id = client_id
        self.client_secret_file = client_secret_file
        self.opener = opener or secure_opener(cafile)
        self.clock = clock
        self._jwt = None
        self._valid_to = 0.0

    def invalidate(self):
        self._jwt = None
        self._valid_to = 0.0

    def get(self) -> str:
        if self._jwt and self.clock() < self._valid_to:
            return self._jwt
        # O_NOFOLLOW+fstat prevent the credentials path being swapped to a
        # symlink/non-regular file between permission check and open.
        fd = os.open(self.client_secret_file,
                     os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
        with os.fdopen(fd, "r", encoding="utf-8") as handle:
            st = os.fstat(handle.fileno())
            if not stat.S_ISREG(st.st_mode) or st.st_mode & 0o077:
                raise ValueError("OIDC device secret must be owner-only regular file")
            secret = handle.read(257).strip()
        if not 24 <= len(secret) <= 256:
            raise ValueError("OIDC device credential missing or invalid length")
        form = parse.urlencode({
            "grant_type": "client_credentials",
            "client_id": self.client_id,
            "client_secret": secret,
        }).encode("utf-8")
        outgoing = request.Request(
            self.token_endpoint, data=form,
            headers={"Content-Type": "application/x-www-form-urlencoded",
                     "Accept": "application/json"}, method="POST",
        )
        # Never emit, persist, print or log the token or client secret.
        with self.opener.open(outgoing, timeout=4) as response:
            if response.status != 200:
                raise ValueError("OIDC token endpoint rejected credentials")
            body = response.read(8193)
        if len(body) > 8192:
            raise ValueError("OIDC token response exceeds size limit")
        raw = json.loads(body)
        token = raw.get("access_token")
        ttl = raw.get("expires_in")
        if (raw.get("token_type", "").lower() != "bearer" or
                not isinstance(token, str) or not 64 <= len(token) <= 8192 or
                type(ttl) is not int or not 30 <= ttl <= 3600):
            raise ValueError("OIDC returned an invalid device token receipt")
        self._jwt = token
        self._valid_to = self.clock() + min(ttl * 0.75, ttl - 15)
        return self._jwt
