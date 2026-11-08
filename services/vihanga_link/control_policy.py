"""Fail-closed flight-control authority and command lifecycle.

This module is independent of transport and airframe. It does not write MAVLink,
open a UART, or authorize physical aircraft actuation by itself. The separate
Pi-local hardware interlock and FC protocol adapter are mandatory for flight.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from hashlib import sha256
import hmac
import json
import math
import re
import secrets
import time
from typing import Any, Callable


ACTIONS = frozenset({
    "ARM", "DISARM", "TAKEOFF", "LAND", "RTL",
    "SET_MODE", "PARAM_SET", "MISSION_UPLOAD", "MISSION_START",
})
NAV_ACTIONS = frozenset({
    "ARM", "TAKEOFF", "RTL", "MISSION_UPLOAD", "MISSION_START",
})
MISSION_ACTIONS = frozenset({"MISSION_UPLOAD", "MISSION_START"})
PREPARE_ACTIONS = frozenset({"PARAM_SET", "MISSION_UPLOAD"})
# Never permit safety bypasses, force-arm, force-disarm or arbitrary param writes.
PARAMETER_BOUNDS = {
    # ArduCopter units for these specific firmware parameters are cm/s,
    # cm/s/s or centimetres. Bounds are conservative but still require
    # on-airframe confirmation before any physical PARAM_SET transport.
    "WPNAV_SPEED": (50, 1000), "WPNAV_ACCEL": (50, 1000),
    "RTL_ALT": (500, 10000), "RTL_SPEED": (50, 1000),
}
PARAMETER_ALLOWLIST = frozenset(PARAMETER_BOUNDS)
MODES = frozenset({"STABILIZE", "ALT_HOLD", "LOITER", "GUIDED", "AUTO",
                   "BRAKE", "RTL", "LAND"})


class ControlDenied(ValueError):
    pass


def _id(value: str) -> bool:
    return isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_-]{3,64}", value) is not None


def _digest(token: str) -> str:
    return sha256(token.encode("utf-8")).hexdigest()


@dataclass(frozen=True)
class ObservedVehicle:
    vehicle_id: str
    system_id: int
    source: str
    heartbeat_age_ms: int
    attitude_age_ms: int
    gps_age_ms: int | None
    gps_fix: int | None
    home_verified: bool
    battery_percent: float | None
    ekf_healthy: bool
    geofence_healthy: bool
    rc_link_healthy: bool
    rc_override_active: bool
    on_ground: bool
    armed: bool
    mode: str
    failsafe_clear: bool

    def require_current(self, vehicle_id: str, system_id: int, *, navigation: bool) -> None:
        # No caller-supplied health assertion is evidence of a physical sensor:
        # the production adapter must derive every field from FC packets.
        if (self.vehicle_id != vehicle_id or self.system_id != system_id or
            self.source not in ("SITL", "HIL", "PHYSICAL_FC") or
            not 0 <= self.heartbeat_age_ms <= 1200 or
            not 0 <= self.attitude_age_ms <= 1200 or
            not self.rc_link_healthy or self.rc_override_active or
            not self.failsafe_clear):
            raise ControlDenied("Stale identity, pilot override, or failsafe state")
        if navigation and (self.gps_fix is None or self.gps_fix < 3 or
                           self.gps_age_ms is None or not 0 <= self.gps_age_ms <= 1500 or
                           not self.home_verified or not self.ekf_healthy or
                           not self.geofence_healthy or
                           self.battery_percent is None or
                           not math.isfinite(self.battery_percent) or
                           self.battery_percent < 25):
            raise ControlDenied("GPS, home, EKF, geofence or reserve not verified")


@dataclass(frozen=True)
class FlightIntent:
    aircraft: str
    request_id: str
    sequence: int
    action: str
    params: dict[str, Any]
    plan_sha256: str
    ttl_ms: int = 3000

    def verify(self) -> None:
        if (not _id(self.aircraft) or
            not isinstance(self.request_id, str) or
            re.fullmatch(r"[a-f0-9]{32}", self.request_id) is None or
            type(self.sequence) is not int or self.sequence < 1 or
            not isinstance(self.action, str) or self.action not in ACTIONS or
            type(self.params) is not dict or
            not isinstance(self.plan_sha256, str) or
            re.fullmatch(r"[a-f0-9]{64}", self.plan_sha256) is None or
            type(self.ttl_ms) is not int or not 500 <= self.ttl_ms <= 5000):
            raise ControlDenied("Invalid typed flight-control intent")
        try:
            canonical = json.dumps(self.params, allow_nan=False, sort_keys=True)
        except (TypeError, ValueError) as exc:
            raise ControlDenied("Non-finite or unsupported command parameter") from exc
        if len(canonical) > 1024:
            raise ControlDenied("Oversize command parameters")
        if self.action in ("ARM", "DISARM", "LAND", "RTL", "MISSION_START"):
            expected = {"revision": self.plan_sha256} if self.action == "MISSION_START" else {}
            if self.params != expected:
                raise ControlDenied("Unexpected parameters for command")
        elif self.action == "TAKEOFF":
            if (set(self.params) != {"relative_alt_m"} or
                type(self.params["relative_alt_m"]) not in (float, int) or
                not 2 <= self.params["relative_alt_m"] <= 30):
                raise ControlDenied("Takeoff altitude must be 2–30 metres")
        elif self.action == "SET_MODE":
            if (self.params.keys() != {"mode"} or
                not isinstance(self.params["mode"], str) or
                self.params["mode"] not in MODES):
                raise ControlDenied("Unsupported flight mode")
        elif self.action == "PARAM_SET":
            name, value = self.params.get("name"), self.params.get("value")
            if (set(self.params) != {"name", "value"} or
                not isinstance(name, str) or name not in PARAMETER_ALLOWLIST or
                type(value) not in (float, int) or not math.isfinite(value) or
                not PARAMETER_BOUNDS[name][0] <= value <= PARAMETER_BOUNDS[name][1]):
                raise ControlDenied("Parameter not approved for guarded write")
        elif self.action == "MISSION_UPLOAD":
            if self.params != {"revision": self.plan_sha256}:
                raise ControlDenied("Mission revision does not match approved digest")

    def digest(self) -> str:
        self.verify()
        return _digest(json.dumps(self.__dict__, sort_keys=True, separators=(",", ":")))


@dataclass
class _Lease:
    vehicle_id: str
    system_id: int
    pilot: str
    approver: str
    plan_sha256: str
    token_sha256: str
    expires_at: float
    last_sequence: int = 0
    control_epoch: int = 0


@dataclass
class _Command:
    intent: FlightIntent
    digest: str
    expires_at: float
    created_at: float
    status: str = "AUTHORIZED_NOT_DISPATCHED"
    pi_received: bool = False
    fc_ack_result: int | None = None
    pi_interlock: bool = False


class FlightControlBroker:
    """Single-aircraft exclusive lease, replay protection and ACK correlation.

    Physical dispatch cannot occur through this class: a separate audited
    command adapter must check the Pi's real hardware interlock and FC state.
    """

    def __init__(self, clock: Callable[[], float] = time.monotonic):
        self.clock = clock
        self._lease: _Lease | None = None
        self._commands: dict[str, _Command] = {}
        self._pending: str | None = None
        self._audit_hash = "0" * 64
        self.audit: list[dict] = []

    def _record(self, event: str, request_id: str = "") -> None:
        entry = {"event": event, "request_id": request_id, "time": self.clock(),
                 "previous": self._audit_hash}
        self._audit_hash = _digest(json.dumps(entry, sort_keys=True))
        entry["hash"] = self._audit_hash
        self.audit.append(entry)

    def acquire(self, *, vehicle_id: str, system_id: int, pilot: str,
                approver: str, plan_sha256: str, control_epoch: int,
                ttl_seconds: int = 20) -> dict:
        if (not _id(vehicle_id) or not 1 <= system_id <= 254 or
            not _id(pilot) or not _id(approver) or pilot == approver or
            not isinstance(plan_sha256, str) or
            re.fullmatch(r"[a-f0-9]{64}", plan_sha256) is None or
            type(control_epoch) is not int or control_epoch < 0 or
            not 5 <= ttl_seconds <= 30):
            raise ControlDenied("Invalid independent pilot approval or lease")
        if self._lease and self.clock() < self._lease.expires_at:
            raise ControlDenied("Another controller holds the aircraft lease")
        token = secrets.token_urlsafe(36)
        self._lease = _Lease(vehicle_id, system_id, pilot, approver,
                             plan_sha256, _digest(token),
                             self.clock() + ttl_seconds, control_epoch=control_epoch)
        self._pending = None
        self._record("LEASE_GRANTED")
        return {"vehicle_id": vehicle_id, "lease_token": token,
                "expires_in": ttl_seconds, "control_epoch": control_epoch}

    def revoke(self, reason: str = "OPERATOR_RELEASE") -> None:
        self._record("LEASE_REVOKED_" + reason)
        self._lease = None
        self._pending = None
        for command in self._commands.values():
            if command.status in ("AUTHORIZED_NOT_DISPATCHED", "SENT_TO_PI", "PI_RECEIVED"):
                command.status = "CANCELLED_LEASE_LOST"

    def _valid_lease(self, vehicle_id: str, token: str, epoch: int) -> _Lease:
        lease = self._lease
        if (not lease or lease.vehicle_id != vehicle_id or
            epoch != lease.control_epoch or self.clock() >= lease.expires_at or
            not isinstance(token, str) or
            not hmac.compare_digest(lease.token_sha256, _digest(token))):
            raise ControlDenied("Missing, expired or wrong-epoch pilot lease")
        return lease

    def submit(self, intent: FlightIntent, *, lease_token: str,
               evidence: ObservedVehicle, epoch: int) -> dict:
        intent.verify()
        lease = self._valid_lease(intent.aircraft, lease_token, epoch)
        if intent.plan_sha256 != lease.plan_sha256:
            raise ControlDenied("Mission revision changed since approval")
        digest = intent.digest()
        prior = self._commands.get(intent.request_id)
        if prior is not None:
            if prior.digest != digest:
                raise ControlDenied("Replay identifier used for different command")
            return self.receipt(intent.request_id)
        if self._pending is not None:
            raise ControlDenied("Only one flight command may be outstanding")
        if intent.sequence != lease.last_sequence + 1:
            raise ControlDenied("Non-monotonic pilot command sequence")
        evidence.require_current(intent.aircraft, lease.system_id,
                                 navigation=intent.action in NAV_ACTIONS)
        if intent.action in PREPARE_ACTIONS and (evidence.armed or
                                                not evidence.on_ground):
            raise ControlDenied("Mission and parameter writes require landed, disarmed aircraft")
        if intent.action == "ARM" and (evidence.armed or not evidence.on_ground):
            raise ControlDenied("ARM requires landed disarmed aircraft")
        if intent.action == "DISARM" and (not evidence.armed or not evidence.on_ground):
            raise ControlDenied("DISARM refused unless the aircraft is on ground")
        if intent.action == "TAKEOFF" and (not evidence.armed or
                                         evidence.mode != "GUIDED"):
            raise ControlDenied("TAKEOFF requires armed GUIDED mode")
        if intent.action == "MISSION_START" and (not evidence.armed or
                                                 evidence.mode != "AUTO"):
            raise ControlDenied("Mission start requires armed AUTO mode")
        if intent.action == "SET_MODE":
            mode = intent.params["mode"]
            if mode in ("AUTO", "GUIDED", "LOITER", "RTL") and not evidence.home_verified:
                raise ControlDenied("Navigation mode requires verified home")
        if intent.action == "RTL" and not evidence.armed:
            raise ControlDenied("RTL requires an armed aircraft")
        lease.last_sequence = intent.sequence
        self._commands[intent.request_id] = _Command(intent, digest,
                                                     self.clock() + intent.ttl_ms / 1000,
                                                     self.clock())
        self._pending = intent.request_id
        self._record("COMMAND_AUTHORIZED", intent.request_id)
        return self.receipt(intent.request_id)

    def transport_sent(self, request_id: str) -> dict:
        cmd = self._require_pending(request_id)
        cmd.status = "SENT_TO_PI"
        self._record("TRANSPORT_SENT", request_id)
        return self.receipt(request_id)

    def pi_result(self, request_id: str, *, accepted: bool, interlock: bool) -> dict:
        cmd = self._require_pending(request_id)
        cmd.pi_received = bool(accepted)
        cmd.pi_interlock = bool(interlock)
        cmd.status = "PI_RECEIVED" if accepted and interlock else "PI_REFUSED"
        if cmd.status == "PI_REFUSED":
            self._pending = None
        self._record(cmd.status, request_id)
        return self.receipt(request_id)

    def fc_result(self, request_id: str, *, result: int, fc_system_id: int,
                  fc_component_id: int, command_id: int) -> dict:
        cmd = self._require_pending(request_id)
        lease = self._lease
        if not cmd.pi_received or not cmd.pi_interlock or lease is None:
            raise ControlDenied("Pi transport acknowledgment is not FC acknowledgment")
        if fc_system_id != lease.system_id or fc_component_id != 1:
            raise ControlDenied("Untrusted FC acknowledgment source")
        expected = {
            "ARM": 400, "DISARM": 400, "TAKEOFF": 22, "LAND": 21,
            "RTL": 20, "SET_MODE": 176, "MISSION_START": 300,
        }.get(cmd.intent.action)
        if expected is None or command_id != expected:
            raise ControlDenied("Unmatched or unsupported physical command acknowledgment")
        if type(result) is not int or not 0 <= result <= 255:
            raise ControlDenied("Invalid MAVLink command result")
        cmd.fc_ack_result = result
        cmd.status = "FC_ACCEPTED_NOT_EXECUTION_VERIFIED" if result == 0 else (
            "FC_IN_PROGRESS" if result == 5 else "FC_REJECTED")
        if result != 5:
            self._pending = None
        self._record(cmd.status, request_id)
        return self.receipt(request_id)

    def _require_pending(self, request_id: str) -> _Command:
        cmd = self._commands.get(request_id)
        if cmd is None or self._pending != request_id:
            raise ControlDenied("No matching pending flight command")
        if self.clock() >= cmd.expires_at:
            cmd.status = "EXPIRED_NO_REPLAY"
            self._pending = None
            self._record(cmd.status, request_id)
            raise ControlDenied("Command expired; never replay automatically")
        return cmd

    def expire(self) -> int:
        now = self.clock()
        expired = 0
        for request_id, cmd in self._commands.items():
            if cmd.status in ("AUTHORIZED_NOT_DISPATCHED", "SENT_TO_PI",
                              "PI_RECEIVED", "FC_IN_PROGRESS") and now >= cmd.expires_at:
                cmd.status = "EXPIRED_NO_REPLAY"
                if self._pending == request_id:
                    self._pending = None
                self._record(cmd.status, request_id)
                expired += 1
        if self._lease and now >= self._lease.expires_at:
            self.revoke("EXPIRED")
        return expired

    def receipt(self, request_id: str) -> dict:
        cmd = self._commands.get(request_id)
        if cmd is None:
            raise ControlDenied("Unknown command")
        return {"request_id": request_id, "action": cmd.intent.action,
                "status": cmd.status, "pi_received": cmd.pi_received,
                "pi_interlock_verified": cmd.pi_interlock,
                "fc_ack_result": cmd.fc_ack_result,
                "flight_executed": False,
                "expires_in_ms": max(0, int((cmd.expires_at - self.clock()) * 1000))}
