"""Supervised command authority model for simulator acceptance.

No physical MAVLink dispatch is implemented here. A transport ACK is not
a flight-controller ACK. This module models the lease, freshness, revision,
sequence and interlock gates required before a separate hardware review.
"""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import hmac
import json
import math
import re
import secrets
import time
from typing import Callable


class AuthorityRejected(ValueError):
    pass


@dataclass(frozen=True)
class VehicleEvidence:
    vehicle_id: str
    system_id: int
    heartbeat_age_ms: int
    attitude_age_ms: int
    source: str
    disarmed: bool
    gps_fix: int
    ekf_healthy: bool
    geofence_healthy: bool
    battery_percent: float
    pilot_override_available: bool
    failsafe_clear: bool

    def require_safe(self, vehicle_id: str, system_id: int) -> None:
        if (self.vehicle_id != vehicle_id or self.system_id != system_id or
            self.source != "SITL" or
            not 0 <= self.heartbeat_age_ms <= 1500 or
            not 0 <= self.attitude_age_ms <= 1500 or
            self.gps_fix < 3 or not self.ekf_healthy or
            not self.geofence_healthy or
            not math.isfinite(self.battery_percent) or
            self.battery_percent < 25 or
            not self.pilot_override_available or not self.failsafe_clear):
            raise AuthorityRejected("Unverified, stale or unsafe simulator vehicle state")


@dataclass
class _Lease:
    pilot: str
    approver: str
    vehicle_id: str
    system_id: int
    plan_digest: str
    token_digest: str
    expires_at: float
    last_sequence: int = 0


class SimulatedControlPlane:
    """Simulator-only command lifecycle with independent logical ACKs.

    The contract deliberately does not expose a physical enable flag or
    accept arbitrary encoded MAVLink bytes.
    """

    ACTIONS = frozenset({
        "PARAM_WRITE_SIM", "MISSION_UPLOAD_SIM", "MISSION_START_SIM"
    })

    def __init__(self, clock: Callable[[], float] = time.monotonic):
        self.clock = clock
        self.leases: dict[str, _Lease] = {}
        self.receipts: dict[str, dict] = {}
        self.pending: dict[str, tuple[str, float]] = {}
        self.loaded_missions: dict[str, str] = {}
        self.parameters: dict[str, dict[str, float]] = {}

    @staticmethod
    def _digest(value: str) -> str:
        return hashlib.sha256(value.encode("utf-8")).hexdigest()

    def acquire(self, vehicle_id: str, system_id: int, pilot: str,
                approver: str, plan_digest: str, ttl_seconds: int = 30) -> dict:
        if (not re.fullmatch(r"[A-Za-z0-9_-]{3,64}", vehicle_id) or
            not 1 <= system_id <= 254 or
            not pilot or not approver or pilot == approver or
            not re.fullmatch(r"[a-f0-9]{64}", plan_digest) or
            not 10 <= ttl_seconds <= 60):
            raise AuthorityRejected("Invalid aircraft, independent approval or lease")
        active = self.leases.get(vehicle_id)
        if active is not None and self.clock() < active.expires_at:
            raise AuthorityRejected("A valid exclusive vehicle lease already exists")
        raw = secrets.token_urlsafe(48)
        self.leases[vehicle_id] = _Lease(
            pilot, approver, vehicle_id, system_id, plan_digest,
            self._digest(raw), self.clock() + ttl_seconds)
        return {"vehicle_id": vehicle_id, "lease_token": raw,
                "lease_expires_at": self.leases[vehicle_id].expires_at,
                "scope": "SIMULATOR_ONLY"}

    def _lease(self, vehicle_id: str, token: str) -> _Lease:
        lease = self.leases.get(vehicle_id)
        if (not lease or not token or self.clock() >= lease.expires_at or
            not hmac.compare_digest(lease.token_digest, self._digest(token))):
            raise AuthorityRejected("Missing, expired or invalid exclusive lease")
        return lease

    def submit(self, *, vehicle_id: str, lease_token: str,
               request_id: str, sequence: int, action: str, payload: dict,
               plan_digest: str, evidence: VehicleEvidence,
               ttl_seconds: int = 5) -> dict:
        lease = self._lease(vehicle_id, lease_token)
        if (action not in self.ACTIONS or
            not re.fullmatch(r"[0-9a-f]{32}", request_id) or
            not 1 <= ttl_seconds <= 10 or
            not isinstance(sequence, int) or isinstance(sequence, bool) or
            not isinstance(payload, dict) or
            not hmac.compare_digest(plan_digest, lease.plan_digest)):
            raise AuthorityRejected("Unsupported action, TTL or approved revision")
        canonical = json.dumps({
            "vehicle_id": vehicle_id, "sequence": sequence, "action": action,
            "payload": payload, "plan_digest": plan_digest,
        }, sort_keys=True, separators=(",", ":"), allow_nan=False)
        digest = self._digest(canonical)
        existing = self.receipts.get(request_id)
        if existing is not None:
            if existing["digest"] != digest:
                raise AuthorityRejected("Idempotency identifier reused with different command")
            return dict(existing["public"])
        if sequence != lease.last_sequence + 1:
            raise AuthorityRejected("Out-of-order simulator command")
        evidence.require_safe(vehicle_id, lease.system_id)
        if action in ("PARAM_WRITE_SIM", "MISSION_UPLOAD_SIM") and not evidence.disarmed:
            raise AuthorityRejected("Parameter and mission preparation require disarmed vehicle")
        if action == "PARAM_WRITE_SIM":
            name, value = payload.get("name"), payload.get("value")
            if (not isinstance(name, str) or
                not re.fullmatch(r"[A-Z][A-Z0-9_]{0,15}", name) or
                not isinstance(value, (int, float)) or isinstance(value, bool) or
                not math.isfinite(value)):
                raise AuthorityRejected("Invalid typed simulator parameter value")
        elif action == "MISSION_UPLOAD_SIM":
            if payload != {"revision_sha256": plan_digest}:
                raise AuthorityRejected("Mission upload revision mismatch")
        elif action == "MISSION_START_SIM":
            if (evidence.disarmed or
                self.loaded_missions.get(vehicle_id) != plan_digest or
                payload != {"revision_sha256": plan_digest}):
                raise AuthorityRejected("Approved mission not loaded or vehicle disarmed")
        lease.last_sequence = sequence
        public = {"request_id": request_id, "status": "SIM_VALIDATED",
                  "pi_transport_ack": False, "fc_result_ack": False,
                  "hardware_actuated": False}
        self.receipts[request_id] = {
            "digest": digest, "public": public, "action": action,
            "payload": payload, "plan_digest": plan_digest,
            "vehicle_id": vehicle_id, "expires_at": min(
                self.clock() + ttl_seconds, lease.expires_at)}
        self.pending[request_id] = (vehicle_id, self.receipts[request_id]["expires_at"])
        return dict(public)

    def observe_pi_ack(self, request_id: str, accepted: bool) -> dict:
        entry = self.receipts.get(request_id)
        if not entry or request_id not in self.pending:
            raise AuthorityRejected("Unknown or completed simulator command")
        if self.clock() >= entry["expires_at"]:
            entry["public"]["status"] = "EXPIRED_SIM"
            self.pending.pop(request_id, None)
            return dict(entry["public"])
        entry["public"]["pi_transport_ack"] = bool(accepted)
        entry["public"]["status"] = "PI_ACCEPTED_SIM" if accepted else "PI_REFUSED_SIM"
        if not accepted:
            self.pending.pop(request_id, None)
        return dict(entry["public"])

    def observe_fc_result(self, request_id: str, success: bool,
                          evidence: VehicleEvidence) -> dict:
        entry = self.receipts.get(request_id)
        if not entry or request_id not in self.pending:
            raise AuthorityRejected("Unknown or completed simulator command")
        if not entry["public"]["pi_transport_ack"]:
            raise AuthorityRejected("PI transport ACK is not FC result ACK")
        if self.clock() >= entry["expires_at"]:
            entry["public"]["status"] = "EXPIRED_SIM"
        else:
            lease = self.leases.get(entry["vehicle_id"])
            if lease is None:
                raise AuthorityRejected("Lease no longer exists")
            evidence.require_safe(entry["vehicle_id"], lease.system_id)
            entry["public"]["fc_result_ack"] = bool(success)
            entry["public"]["status"] = "FC_ACCEPTED_SIM" if success else "FC_REJECTED_SIM"
            if success:
                vehicle = entry["vehicle_id"]
                if entry["action"] == "MISSION_UPLOAD_SIM":
                    self.loaded_missions[vehicle] = entry["plan_digest"]
                elif entry["action"] == "PARAM_WRITE_SIM":
                    self.parameters.setdefault(vehicle, {})[
                        entry["payload"]["name"]] = entry["payload"]["value"]
        self.pending.pop(request_id, None)
        return dict(entry["public"])

    def status(self, request_id: str) -> dict:
        if request_id not in self.receipts:
            raise AuthorityRejected("Unknown simulator command")
        return dict(self.receipts[request_id]["public"])
