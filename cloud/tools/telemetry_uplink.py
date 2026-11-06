"""Reusable cloud telemetry sidecar client for an existing Pi telemetry reader.

Consumes already-normalized observations from the existing UART-owning
application. Never opens a serial port, sends MAVLink, or replays stale
buffers as if they were live. Caller supplies a short-lived JWT provider.
"""
from __future__ import annotations

from collections.abc import Callable
from urllib.parse import urlsplit

import httpx

from cloud.app.main import TelemetryObservation


class ObservationUplink:
    def __init__(self, *, base_url: str, token_supplier: Callable[[], str],
                 transport: httpx.BaseTransport | None = None,
                 test_allow_http: bool = False):
        parts = urlsplit(base_url)
        if (not parts.hostname or parts.username or parts.password or
                parts.query or parts.fragment or parts.path not in ("", "/") or
                parts.scheme not in (("https", "http") if test_allow_http else ("https",))):
            raise ValueError("Require credential-free trusted cloud HTTPS origin")
        self.client = httpx.Client(base_url=base_url, transport=transport,
                                   timeout=httpx.Timeout(5.0), follow_redirects=False)
        self.token_supplier = token_supplier
        self.epoch: int | None = None
        self.sequence = 0

    def headers(self):
        token = self.token_supplier()
        if not token or len(token) < 16:
            raise ValueError("Missing short-lived device principal")
        return {"Authorization": "Bearer " + token}

    def begin_session(self):
        response = self.client.post("/api/v1/device/telemetry/session",
                                    headers=self.headers())
        response.raise_for_status()
        receipt = response.json()
        epoch = receipt["session_epoch"]
        if type(epoch) is not int or epoch < 1:
            raise ValueError("Invalid server session epoch")
        self.epoch = epoch
        self.sequence = 0
        return epoch

    def send_latest(self, observation: dict) -> int:
        if self.epoch is None:
            raise ValueError("No established device telemetry session")
        # Never accept client-supplied authority counters or arbitrary fields.
        payload = TelemetryObservation.model_validate({
            **observation, "epoch": self.epoch, "sequence": self.sequence + 1,
        })
        reply = self.client.post("/api/v1/device/telemetry",
                                 headers=self.headers(),
                                 json=payload.model_dump(mode="json"))
        # On transport failure: do NOT retry or replay an old frame. The
        # caller may submit a newly captured observation after recovery.
        reply.raise_for_status()
        accepted = reply.json()
        if accepted["accepted"] is not True:
            raise ValueError("Cloud did not accept observation")
        self.sequence += 1
        return self.sequence

    def close(self):
        self.client.close()
