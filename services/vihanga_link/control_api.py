"""Private, role-separated cloud flight-control authority API.

Simulator-oriented command approval only in this release. Never dispatches a
physical flight command or raw MAVLink. Independent pilot and safety-approver
credentials are REQUIRED; telemetry-viewer JWTs have no authority here.
"""
from __future__ import annotations

from collections import deque
from dataclasses import dataclass
import hmac
import os
import re
import secrets
import time
from typing import Any, Callable

from argon2 import PasswordHasher
from argon2.exceptions import VerifyMismatchError, VerificationError
from fastapi import FastAPI, Header, HTTPException
import jwt
from jwt import InvalidTokenError
from pydantic import BaseModel, Field

from .control_policy import (
    ControlDenied, FlightControlBroker, FlightIntent, ObservedVehicle,
)
from .control_transport_probe import ControlTransportProbe, ProbeDenied
from .typed_command_downlink import outbound_command, PHYSICAL_ACTIONS, PI_RECEIPT_FIELDS, FC_RECEIPT_FIELDS
import json


@dataclass(frozen=True)
class ControlSettings:
    enabled: bool = False
    pilot_name: str = ""
    pilot_password_hash: str = ""
    approver_name: str = ""
    approver_password_hash: str = ""
    jwt_key: str = ""
    typed_downlink: bool = False
    physical_release_accepted: bool = False

    @staticmethod
    def from_env() -> "ControlSettings":
        return ControlSettings(
            enabled=os.getenv("VIHANGA_CONTROL_SIMULATOR_ONLY", "0") == "1",
            pilot_name=os.getenv("VIHANGA_CONTROL_PILOT_NAME", ""),
            pilot_password_hash=os.getenv("VIHANGA_CONTROL_PILOT_PASSWORD_HASH", ""),
            approver_name=os.getenv("VIHANGA_CONTROL_APPROVER_NAME", ""),
            approver_password_hash=os.getenv("VIHANGA_CONTROL_APPROVER_PASSWORD_HASH", ""),
            jwt_key=os.getenv("VIHANGA_CONTROL_JWT_KEY", ""),
            typed_downlink=os.getenv("VIHANGA_TYPED_COMMAND_DOWNLINK", "0") == "1",
            physical_release_accepted=os.getenv(
                "VIHANGA_PHYSICAL_RELEASE_ACCEPTED", "0") == "1",
        )

    def validate(self) -> None:
        if self.typed_downlink and not self.enabled:
            raise ValueError("Typed downlink requires independently authenticated control roles")
        if self.physical_release_accepted and (not self.enabled or not self.typed_downlink):
            raise ValueError("Physical flight acceptance requires typed control and independent roles")
        if not self.enabled:
            return
        if (not re.fullmatch(r"[A-Za-z0-9_-]{3,64}", self.pilot_name) or
            not re.fullmatch(r"[A-Za-z0-9_-]{3,64}", self.approver_name) or
            self.pilot_name == self.approver_name or
            not self.pilot_password_hash.startswith("$argon2id$") or
            not self.approver_password_hash.startswith("$argon2id$") or
            len(self.jwt_key) < 48):
            raise ValueError("Control requires independent Argon2id roles and an independent JWT key")


class RoleLogin(BaseModel):
    username: str = Field(min_length=3, max_length=64)
    password: str = Field(min_length=16, max_length=512)
    role: str = Field(pattern=r"^(pilot|approver)$")


class ApprovalRequest(BaseModel):
    plan_sha256: str = Field(pattern=r"^[a-f0-9]{64}$")


class LeaseRequest(ApprovalRequest):
    approval_id: str = Field(pattern=r"^[a-f0-9]{32}$")


class FlightRequest(BaseModel):
    request_id: str = Field(pattern=r"^[a-f0-9]{32}$")
    sequence: int = Field(ge=1)
    action: str = Field(min_length=3, max_length=40)
    params: dict[str, Any] = Field(default_factory=dict)
    plan_sha256: str = Field(pattern=r"^[a-f0-9]{64}$")
    ttl_ms: int = Field(default=3000, ge=500, le=5000)


class ControlContext:
    def __init__(self, settings: ControlSettings, vehicle_id: str, state: Any,
                 clock: Callable[[], float] = time.monotonic):
        self.settings = settings
        self.vehicle_id = vehicle_id
        self.state = state
        self.clock = clock
        self.broker = FlightControlBroker(clock=clock)
        self.shadow_transport = ControlTransportProbe(clock=clock)
        self.password_hasher = PasswordHasher()
        self.login_attempts: deque[float] = deque(maxlen=20)
        # Single-use, FC-epoch- and plan-revision-bound consent minted by a
        # separately authenticated safety approver.
        self.approvals: dict[str, tuple[str, str, int, float]] = {}
        self.evidence_provider: Callable[[], ObservedVehicle] = self._physical_evidence

    def observe_pi_command_receipt(self, raw: str) -> bool:
        """Validate reports from the currently authenticated Pi WSS only.

        A Pi receipt is never a substitute for a real matching FC COMMAND_ACK.
        Caller must hold the LinkState session lock and confirm the sender.
        """
        if not self.settings.typed_downlink or not isinstance(raw, str) or len(raw) > 1024:
            return False
        try:
            msg = json.loads(raw)
        except (ValueError, TypeError):
            return False
        if not isinstance(msg, dict) or msg.get("vehicle_id") != self.vehicle_id:
            return False
        if type(msg.get("control_epoch")) is not int or msg["control_epoch"] != self.state.control_epoch:
            return False
        request_id = msg.get("request_id")
        if not isinstance(request_id, str):
            return False
        try:
            prior = self.broker.receipt(request_id)
            if prior["status"] not in ("SENT_TO_PI", "PI_RECEIVED", "FC_IN_PROGRESS"):
                return False
            if msg.get("type") == "typed_control_pi_receipt" and set(msg) == PI_RECEIPT_FIELDS:
                if msg.get("action") != prior["action"] or msg.get("fc_ack_received") is not False:
                    return False
                if msg.get("status") == "PI_UART_SENT_ACK_PENDING" and msg.get("interlock") is True:
                    self.broker.pi_result(request_id, accepted=True, interlock=True)
                    return True
                if (msg.get("status") in ("PI_REFUSED_LOCAL_SAFETY",
                        "PI_REFUSED_BUSY", "PI_REFUSED_REPLAY_OR_EPOCH") and
                        msg.get("interlock") is False):
                    self.broker.pi_result(request_id, accepted=False, interlock=False)
                    return True
            if msg.get("type") == "typed_control_fc_receipt" and set(msg) == FC_RECEIPT_FIELDS:
                if msg.get("fc_ack_received") is not True:
                    return False
                self.broker.fc_result(request_id, result=msg.get("result"),
                    fc_system_id=msg.get("fc_system_id"),
                    fc_component_id=msg.get("fc_component_id"),
                    command_id=msg.get("command_id"))
                return True
        except (ControlDenied, ValueError, TypeError):
            return False
        return False

    def observe_fc_safety_evidence(self, raw: str) -> bool:
        """Read an FC-owned safety snapshot only from authenticated Pi WSS.

        No HTTP caller can supply these fields. A report does NOT establish
        hardware airworthiness or allow an RC override to be ignored.
        """
        if not isinstance(raw, str) or len(raw) > 4096:
            return False
        try:
            msg = json.loads(raw)
        except (TypeError, ValueError):
            return False
        if not isinstance(msg, dict) or set(msg) != {
                "type", "vehicle_id", "control_epoch", "sequence",
                "reported_at_unix_ms", "observation", "missing_fc_messages",
                "physical_control_qualified", "local_hardware_interlock_active"}:
            return False
        if (msg["type"] != "fc_safety_evidence" or
            msg["vehicle_id"] != self.vehicle_id or
            type(msg["control_epoch"]) is not int or
            msg["control_epoch"] != self.state.control_epoch or
            type(msg["sequence"]) is not int or msg["sequence"] < 1 or
            msg["sequence"] <= self.state.fc_safety_report_sequence or
            type(msg["reported_at_unix_ms"]) is not int or
            abs(int(time.time()*1000) - msg["reported_at_unix_ms"]) > 3000 or
            msg["physical_control_qualified"] is not False or
            type(msg["local_hardware_interlock_active"]) is not bool or
            not isinstance(msg["missing_fc_messages"], list) or
            len(msg["missing_fc_messages"]) > 16 or
            any(not isinstance(x, str) or len(x) > 40
                for x in msg["missing_fc_messages"])):
            return False
        fields = msg["observation"]
        if not isinstance(fields, dict) or set(fields) != set(ObservedVehicle.__dataclass_fields__):
            return False
        try:
            snapshot = ObservedVehicle(**fields)
        except (ValueError, TypeError):
            return False
        if (snapshot.vehicle_id != self.vehicle_id or
            snapshot.system_id != self.state.fc_system_id or
            snapshot.source != "PHYSICAL_FC"):
            return False
        boolean_fields = ("home_verified", "ekf_healthy", "geofence_healthy",
                          "rc_link_healthy", "rc_override_active", "on_ground",
                          "armed", "failsafe_clear")
        if any(type(getattr(snapshot, key)) is not bool for key in boolean_fields):
            return False
        if (any(type(getattr(snapshot, key)) is not int or
                getattr(snapshot, key) < 0 for key in
                ("heartbeat_age_ms", "attitude_age_ms")) or
            snapshot.gps_age_ms is not None and (
                type(snapshot.gps_age_ms) is not int or snapshot.gps_age_ms < 0) or
            snapshot.battery_percent is not None and (
                type(snapshot.battery_percent) not in (int, float) or
                not 0 <= snapshot.battery_percent <= 100)):
            return False
        self.state.fc_safety_report = msg
        self.state.fc_safety_report_at = self.clock()
        self.state.fc_safety_report_sequence = msg["sequence"]
        return True

    def _physical_evidence(self) -> ObservedVehicle:
        """Unknown safety inputs stay false, never operator-supplied.

        A dedicated Pi hardware switch, physical RC evidence, EKF/fence and
        FC-owned command acknowledgments are NOT integrated yet.
        """
        now = self.clock()
        def age(stamp: float) -> int:
            return int((now - stamp) * 1000) if self.state.connected and stamp >= 0 else 999999
        report = self.state.fc_safety_report
        if (self.settings.typed_downlink and isinstance(report, dict) and
            self.state.connected and self.state.fresh(self.state.last_heartbeat) and
            report.get("control_epoch") == self.state.control_epoch and
            self.state.fc_safety_report_at >= 0 and
            0 <= self.clock() - self.state.fc_safety_report_at <= 1.5):
            try:
                snap = ObservedVehicle(**report["observation"])
                lag = int(max(0, self.clock() - self.state.fc_safety_report_at) * 1000)
                # Combine Pi and relay freshness: never trust a smaller age
                # from one link as proof that the other is current.
                from dataclasses import replace
                return replace(
                    snap,
                    heartbeat_age_ms=max(snap.heartbeat_age_ms + lag, age(self.state.last_heartbeat)),
                    attitude_age_ms=max(snap.attitude_age_ms + lag, age(self.state.last_attitude)),
                    gps_age_ms=(None if snap.gps_age_ms is None else
                        max(snap.gps_age_ms + lag, age(self.state.last_gps_raw_at))),
                    home_verified=(snap.home_verified and
                        self.state.last_valid_home_at >= 0),
                )
            except (KeyError, ValueError, TypeError):
                pass
        return ObservedVehicle(
            vehicle_id=self.vehicle_id, system_id=self.state.fc_system_id,
            source="PHYSICAL_FC",
            heartbeat_age_ms=age(self.state.last_heartbeat),
            attitude_age_ms=age(self.state.last_attitude),
            gps_age_ms=age(self.state.last_gps_raw_at),
            gps_fix=self.state.gps_fix_type,
            home_verified=self.state.fresh(self.state.last_valid_home_at),
            battery_percent=None, ekf_healthy=False, geofence_healthy=False,
            rc_link_healthy=False, rc_override_active=False,
            on_ground=False, armed=False, mode="", failsafe_clear=False,
        )

    def jwt_for(self, role: str) -> str:
        user = self.settings.pilot_name if role == "pilot" else self.settings.approver_name
        now = int(time.time())
        return jwt.encode({
            "sub": user, "role": role, "scope": "control:approve" if role == "approver"
                else "control:pilot",
            "iss": "vihanga-control", "aud": "vihanga-control-lease",
            "iat": now, "nbf": now, "exp": now + 120,
        }, self.settings.jwt_key, algorithm="HS256")

    def identity(self, token: str, role: str) -> str:
        if not token or not self.settings.enabled:
            raise HTTPException(status_code=403, detail="Flight-control authority unavailable")
        try:
            payload = jwt.decode(
                token, self.settings.jwt_key, algorithms=["HS256"],
                issuer="vihanga-control", audience="vihanga-control-lease",
                options={"require": ["sub", "role", "scope", "exp", "iat", "nbf"]})
            expected = self.settings.pilot_name if role == "pilot" else self.settings.approver_name
            if (payload["sub"] != expected or payload["role"] != role or
                payload["scope"] != ("control:approve" if role == "approver" else "control:pilot")):
                raise InvalidTokenError("Role mismatch")
            return expected
        except (InvalidTokenError, ValueError, TypeError) as exc:
            raise HTTPException(status_code=401, detail="Invalid role-specific control token") from exc


def install_control_api(app: FastAPI, vehicle_id: str, state: Any,
                        settings: ControlSettings | None = None) -> ControlContext:
    cfg = settings or ControlSettings.from_env()
    cfg.validate()
    context = ControlContext(cfg, vehicle_id, state)
    app.state.control = context

    def token_from_auth(value: str) -> str:
        return value[7:] if value.startswith("Bearer ") else ""

    def assert_vehicle(value: str) -> None:
        if value != context.vehicle_id:
            raise HTTPException(status_code=404, detail="Unknown aircraft")

    @app.get("/api/v1/flight-control/status")
    async def flight_control_status() -> dict:
        return {
            "mode": "SIMULATOR_AUTHORITY_ONLY" if cfg.enabled else "DISABLED",
            "physical_command_dispatch": False,
            "native_mavlink_command_tunnel": False,
            "typed_arm_disarm_downlink_configured": cfg.typed_downlink,
            "physical_release_flag_configured": cfg.physical_release_accepted,
            "hardware_actuation_qualified": False,
            "pilot_to_pi_shadow_probe": bool(cfg.enabled),
            "fc_safety_report_received": bool(state.fc_safety_report),
            "fc_safety_report_age_ms": (int(max(0, context.clock() -
                state.fc_safety_report_at)*1000)
                if state.fc_safety_report_at >= 0 else None),
            "physical_release_accepted": cfg.physical_release_accepted,
            "pilot_role_required": True,
            "independent_approver_required": True,
            "pi_hardware_interlock_verified": False,
            "vehicle_evidence_complete": False,
            "flight_control_enabled": False,
        }

    @app.get("/api/v1/vehicles/{vehicle}/control/safety")
    async def flight_control_safety(
        vehicle: str, authorization: str = Header(default=""),
    ) -> dict:
        """Operator-readable FC evidence diagnostic, never an arming bypass."""
        assert_vehicle(vehicle)
        context.identity(token_from_auth(authorization), "pilot")
        snapshot = context._physical_evidence()
        try:
            snapshot.require_current(vehicle, state.fc_system_id, navigation=True)
            candidate = bool(snapshot.on_ground and not snapshot.armed)
        except ControlDenied:
            candidate = False
        report = state.fc_safety_report if isinstance(state.fc_safety_report, dict) else {}
        age_ms = (int(max(0, context.clock() - state.fc_safety_report_at) * 1000)
                  if state.fc_safety_report_at >= 0 else None)
        return {
            "vehicle_id": vehicle,
            "device_online": state.connected,
            "control_epoch": state.control_epoch,
            "fc_evidence_age_ms": age_ms,
            "missing_fc_messages": report.get("missing_fc_messages", []),
            "fc_evidence": {
                "heartbeat_age_ms": snapshot.heartbeat_age_ms,
                "attitude_age_ms": snapshot.attitude_age_ms,
                "gps_fix": snapshot.gps_fix,
                "home_verified": snapshot.home_verified,
                "battery_percent": snapshot.battery_percent,
                "ekf_healthy": snapshot.ekf_healthy,
                "geofence_healthy": snapshot.geofence_healthy,
                "rc_link_healthy": snapshot.rc_link_healthy,
                "rc_override_active": snapshot.rc_override_active,
                "on_ground": snapshot.on_ground,
                "armed": snapshot.armed,
                "mode": snapshot.mode,
                "failsafe_clear": snapshot.failsafe_clear,
            },
            "candidate_preflight_evidence_complete": candidate,
            "independent_hardware_acceptance_passed": False,
            "flight_control_enabled": False,
        }

    @app.post("/api/v1/vehicles/{vehicle}/control/transport/probe")
    async def probe_control_transport(
        vehicle: str, authorization: str = Header(default=""),
    ) -> dict:
        """Pilot-scoped non-actuating round-trip, not an aircraft command."""
        assert_vehicle(vehicle)
        context.identity(token_from_auth(authorization), "pilot")
        try:
            return await context.shadow_transport.issue(state, vehicle)
        except ProbeDenied as exc:
            raise HTTPException(status_code=409, detail=str(exc)) from exc

    @app.get("/api/v1/vehicles/{vehicle}/control/transport/probe/{request_id}")
    async def control_transport_receipt(
        vehicle: str, request_id: str,
        authorization: str = Header(default=""),
    ) -> dict:
        assert_vehicle(vehicle)
        context.identity(token_from_auth(authorization), "pilot")
        try:
            return context.shadow_transport.receipt(request_id, state.control_epoch)
        except ProbeDenied as exc:
            raise HTTPException(status_code=404, detail=str(exc)) from exc

    @app.post("/api/v1/flight-control/auth/login")
    async def control_login(body: RoleLogin) -> dict:
        if not cfg.enabled:
            raise HTTPException(status_code=403, detail="Control authority disabled")
        now = context.clock()
        while context.login_attempts and now - context.login_attempts[0] > 60:
            context.login_attempts.popleft()
        if len(context.login_attempts) >= 5:
            raise HTTPException(status_code=429, detail="Control login throttled")
        context.login_attempts.append(now)
        user = cfg.pilot_name if body.role == "pilot" else cfg.approver_name
        hashed = cfg.pilot_password_hash if body.role == "pilot" else cfg.approver_password_hash
        matched = False
        if hmac.compare_digest(body.username, user):
            try:
                matched = context.password_hasher.verify(hashed, body.password)
            except (VerifyMismatchError, VerificationError):
                pass
        if not matched:
            raise HTTPException(status_code=401, detail="Control authentication failed")
        return {"access_token": context.jwt_for(body.role), "token_type": "bearer",
                "role": body.role, "expires_in": 120, "physical_dispatch": False}

    @app.post("/api/v1/vehicles/{vehicle}/control/approvals")
    async def approve_plan(
        vehicle: str, body: ApprovalRequest,
        authorization: str = Header(default=""),
    ) -> dict:
        assert_vehicle(vehicle)
        approver = context.identity(token_from_auth(authorization), "approver")
        if not state.connected or not state.fresh(state.last_heartbeat):
            raise HTTPException(status_code=409, detail="Fresh physical FC evidence required")
        now = context.clock()
        context.approvals = {
            nonce: entry for nonce, entry in context.approvals.items()
            if now < entry[3] and entry[2] == state.control_epoch
        }
        if len(context.approvals) >= 8:
            raise HTTPException(status_code=429, detail="Outstanding approvals limit")
        nonce = secrets.token_hex(16)
        context.approvals[nonce] = (
            approver, body.plan_sha256, state.control_epoch, now + 30.0)
        return {"approval_id": nonce, "plan_sha256": body.plan_sha256,
                "expires_in": 30, "vehicle_id": vehicle,
                "control_epoch": state.control_epoch, "physical_dispatch": False}

    @app.post("/api/v1/vehicles/{vehicle}/control/lease")
    async def control_lease(
        vehicle: str, body: LeaseRequest,
        authorization: str = Header(default=""),
    ) -> dict:
        assert_vehicle(vehicle)
        pilot = context.identity(token_from_auth(authorization), "pilot")
        if not state.connected or not state.fresh(state.last_heartbeat):
            raise HTTPException(status_code=409, detail="Fresh physical FC evidence required")
        record = context.approvals.get(body.approval_id)
        if (not record or record[1] != body.plan_sha256 or
            record[2] != state.control_epoch or context.clock() >= record[3]):
            raise HTTPException(status_code=403, detail="Missing, stale or wrong-revision safety approval")
        approver = record[0]
        if pilot == approver:
            raise HTTPException(status_code=403, detail="Independent approval required")
        # Approval is single-use even if the lease attempt fails.
        del context.approvals[body.approval_id]
        try:
            return context.broker.acquire(
                vehicle_id=vehicle, system_id=state.fc_system_id,
                pilot=pilot, approver=approver,
                plan_sha256=body.plan_sha256, control_epoch=state.control_epoch)
        except ControlDenied as exc:
            raise HTTPException(status_code=409, detail=str(exc)) from exc

    @app.post("/api/v1/vehicles/{vehicle}/control/commands")
    async def control_commands(
        vehicle: str, body: FlightRequest,
        authorization: str = Header(default=""),
        x_control_lease: str = Header(default=""),
    ) -> dict:
        assert_vehicle(vehicle)
        context.identity(token_from_auth(authorization), "pilot")
        # No raw MAVLink frame or arbitrary GCS command enters this route.
        # The independent pilot/approver lease and FC-sourced evidence are
        # required BEFORE even a typed ARM/DISARM downlink can be emitted.
        intent = FlightIntent(vehicle, body.request_id, body.sequence,
                              body.action, body.params, body.plan_sha256, body.ttl_ms)
        try:
            if not cfg.typed_downlink:
                return context.broker.submit(
                    intent, lease_token=x_control_lease,
                    evidence=context.evidence_provider(), epoch=state.control_epoch)
            if intent.action not in PHYSICAL_ACTIONS:
                raise ControlDenied("Typed live downlink currently supports ARM/DISARM only")
            if not cfg.physical_release_accepted:
                raise ControlDenied("Physical flight release has not passed on-site acceptance")
            async with state.lock:
                if (not state.connected or state.device_socket is None or
                        not state.fresh(state.last_heartbeat)):
                    raise ControlDenied("Fresh authenticated Pi and FC required")
                snapshot_epoch = state.control_epoch
                receipt = context.broker.submit(
                    intent, lease_token=x_control_lease,
                    evidence=context.evidence_provider(), epoch=snapshot_epoch)
                if receipt["status"] != "AUTHORIZED_NOT_DISPATCHED":
                    return receipt  # Replay returns existing receipt, never re-sends.
                try:
                    await state.device_socket.send_json(
                        outbound_command(intent, vehicle, snapshot_epoch))
                except (OSError, RuntimeError):
                    context.broker.revoke("DEVICE_TRANSPORT_FAILED")
                    raise HTTPException(status_code=503, detail="Command downlink failed; never replay automatically")
                return context.broker.transport_sent(intent.request_id)
        except ControlDenied as exc:
            raise HTTPException(status_code=409, detail=str(exc)) from exc

    @app.get("/api/v1/vehicles/{vehicle}/control/commands/{request_id}")
    async def control_receipt(vehicle: str, request_id: str,
                              authorization: str = Header(default="")) -> dict:
        assert_vehicle(vehicle)
        context.identity(token_from_auth(authorization), "pilot")
        context.broker.expire()
        try:
            return context.broker.receipt(request_id)
        except ControlDenied as exc:
            raise HTTPException(status_code=404, detail=str(exc)) from exc

    @app.post("/api/v1/vehicles/{vehicle}/control/revoke")
    async def control_revoke(vehicle: str,
                             authorization: str = Header(default="")) -> dict:
        assert_vehicle(vehicle)
        context.identity(token_from_auth(authorization), "pilot")
        context.broker.revoke("PILOT_RELEASE")
        context.approvals.clear()
        return {"status": "REVOKED", "physical_command_dispatch": False}

    return context
