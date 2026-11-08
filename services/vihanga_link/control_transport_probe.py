"""Non-actuating Oracle ↔ Pi control-plane transport challenge.

This path does NOT transport commands or MAVLink, and cannot open or write the
Pixhawk UART. It exists solely to establish that a separately authenticated
pilot request can be correlated with the current authenticated Pi WebSocket.
"""
from __future__ import annotations

from dataclasses import dataclass
import hmac
import json
import re
import secrets
import time
from typing import Any, Callable


HEX32 = re.compile(r"[0-9a-f]{32}")
PROBE_TTL_SECONDS = 4.0
PROBE_FIELDS = frozenset({
    "type", "vehicle_id", "request_id", "control_epoch", "challenge",
})
ACK_FIELDS = PROBE_FIELDS | frozenset({
    "status", "uart_touched", "fc_ack_received", "flight_control_enabled",
})
NO_ACTUATION = "PI_RECEIVED_PROBE_NO_UART"


class ProbeDenied(ValueError):
    pass


def pi_probe_reply(message: Any, vehicle_id: str) -> dict | None:
    """Return a fixed receipt without touching serial ports or pilot state.

    Never accept an action, arguments, MAVLink bytes, mission, parameter or
    operator-supplied vehicle identifier other than the configured aircraft.
    """
    if not isinstance(message, dict) or set(message) != PROBE_FIELDS:
        return None
    if message.get("type") != "control_transport_probe" or message.get("vehicle_id") != vehicle_id:
        return None
    request_id, challenge, epoch = (
        message.get("request_id"), message.get("challenge"),
        message.get("control_epoch"))
    if (not isinstance(request_id, str) or HEX32.fullmatch(request_id) is None or
        not isinstance(challenge, str) or HEX32.fullmatch(challenge) is None or
        type(epoch) is not int or not 1 <= epoch < 2**63):
        return None
    return {
        "type": "control_transport_probe_ack",
        "vehicle_id": vehicle_id, "request_id": request_id,
        "control_epoch": epoch, "challenge": challenge,
        "status": NO_ACTUATION, "uart_touched": False,
        "fc_ack_received": False, "flight_control_enabled": False,
    }


@dataclass
class _Probe:
    request_id: str
    challenge: str
    vehicle_id: str
    epoch: int
    created_at: float
    status: str = "WAITING_FOR_PI"


class ControlTransportProbe:
    """Single-outstanding, bounded, epoch-bound, no-replay WSS round-trip."""

    def __init__(self, clock: Callable[[], float] = time.monotonic):
        self.clock = clock
        self.probes: dict[str, _Probe] = {}
        self.pending: str | None = None
        self.last_issued_at = -1.0
        self.issued_count = 0

    def _resolve(self, record: _Probe, epoch: int) -> None:
        if record.status != "WAITING_FOR_PI":
            return
        if record.epoch != epoch:
            record.status = "CANCELLED_EPOCH_CHANGED"
        elif self.clock() - record.created_at >= PROBE_TTL_SECONDS:
            record.status = "EXPIRED_NO_REPLAY"
        else:
            return
        if self.pending == record.request_id:
            self.pending = None

    def reset_device_session(self) -> None:
        for record in self.probes.values():
            if record.status == "WAITING_FOR_PI":
                record.status = "CANCELLED_DEVICE_SESSION"
        self.pending = None
        self.issued_count = 0
        self.last_issued_at = -1.0

    def receipt(self, request_id: str, epoch: int) -> dict:
        record = self.probes.get(request_id)
        if record is None:
            raise ProbeDenied("Unknown control-plane probe")
        self._resolve(record, epoch)
        return {
            "request_id": record.request_id,
            "vehicle_id": record.vehicle_id,
            "control_epoch": record.epoch,
            "status": record.status,
            "pi_transport_confirmed": record.status == "PI_ECHO_NO_ACTUATION",
            "uart_touched": False,
            "fc_ack_received": False,
            "physical_command_dispatch": False,
            "flight_control_enabled": False,
        }

    async def issue(self, state: Any, vehicle_id: str) -> dict:
        """Send only a fixed challenge over the already-authenticated Pi WSS."""
        async with state.lock:
            if not state.connected or state.device_socket is None:
                raise ProbeDenied("Pi authenticated WebSocket required")
            epoch = state.control_epoch
            if type(epoch) is not int or epoch < 1:
                raise ProbeDenied("Invalid device session epoch")
            if self.pending is not None:
                self._resolve(self.probes[self.pending], epoch)
                if self.pending is not None:
                    raise ProbeDenied("Another transport probe is outstanding")
            now = self.clock()
            if (self.issued_count >= 3 or
                (self.last_issued_at >= 0 and now - self.last_issued_at < 2.0)):
                raise ProbeDenied("Control transport probe rate limit")
            request_id = secrets.token_hex(16)
            challenge = secrets.token_hex(16)
            record = _Probe(request_id, challenge, vehicle_id, epoch, now)
            self.probes[request_id] = record
            self.pending = request_id
            self.issued_count += 1
            self.last_issued_at = now
            if len(self.probes) > 16:
                for old_id in tuple(self.probes):
                    if old_id != request_id:
                        del self.probes[old_id]
                        break
            try:
                await state.device_socket.send_json({
                    "type": "control_transport_probe",
                    "vehicle_id": vehicle_id, "request_id": request_id,
                    "control_epoch": epoch, "challenge": challenge,
                })
            except (OSError, RuntimeError) as exc:
                record.status = "TRANSPORT_FAILED"
                self.pending = None
                raise ProbeDenied("Control-plane transport unavailable") from exc
            return self.receipt(request_id, epoch)

    def observe_ack(self, raw: str, *, epoch: int) -> bool:
        """Ignore forged, late, wrong-epoch, or non-no-actuation ACKs."""
        if not isinstance(raw, str) or len(raw) > 512:
            return False
        try:
            message = json.loads(raw)
        except (ValueError, TypeError):
            return False
        if not isinstance(message, dict) or set(message) != ACK_FIELDS:
            return False
        request_id = message.get("request_id")
        if not isinstance(request_id, str) or request_id != self.pending:
            return False
        record = self.probes.get(request_id)
        if record is None:
            return False
        self._resolve(record, epoch)
        if record.status != "WAITING_FOR_PI":
            return False
        if (message.get("type") != "control_transport_probe_ack" or
            message.get("vehicle_id") != record.vehicle_id or
            type(message.get("control_epoch")) is not int or
            message["control_epoch"] != record.epoch or
            not isinstance(message.get("challenge"), str) or
            not hmac.compare_digest(message["challenge"], record.challenge) or
            message.get("status") != NO_ACTUATION or
            message.get("uart_touched") is not False or
            message.get("fc_ack_received") is not False or
            message.get("flight_control_enabled") is not False):
            return False
        record.status = "PI_ECHO_NO_ACTUATION"
        self.pending = None
        return True
