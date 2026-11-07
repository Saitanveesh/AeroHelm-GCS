"""Vihanga private MAVLink relay: complete telemetry fan-out, no FC command path.

Deploy as a separate private service, not as an implicit replacement for
the running Oracle M1. It accepts exactly one authenticated Pi UART publisher.
"""
from __future__ import annotations

import asyncio
from collections import Counter
from dataclasses import dataclass, field
import hashlib
import hmac
import os
import re
import struct
import time
from typing import Any

from argon2 import PasswordHasher
from argon2.exceptions import VerifyMismatchError, VerificationError
from fastapi import FastAPI, Header, HTTPException, WebSocket, WebSocketDisconnect
from .mission_preview import PreviewRequest, preview
from fastapi.responses import JSONResponse
from fastapi.security import HTTPBearer
import jwt
from jwt import InvalidTokenError
from pydantic import BaseModel, Field
from starlette.middleware.trustedhost import TrustedHostMiddleware
from pymavlink.dialects.v20 import ardupilotmega as mavlink_dialect

APP_MODE = "hardware-telemetry-only"
MAX_FRAME = 300
FRESH_SECONDS = 3.0
TOKEN_LIFETIME_SECONDS = 600
DEVICE_ID = re.compile(r"^[A-Za-z0-9_-]{3,64}$")


@dataclass(frozen=True)
class Config:
    vehicle_id: str
    operator_name: str
    operator_password_hash: str
    jwt_key: str
    device_token_sha256: str
    allowed_hosts: tuple[str, ...]
    fc_system_id: int = 1

    @staticmethod
    def from_env() -> "Config":
        cfg = Config(
            vehicle_id=os.getenv("VIHANGA_VEHICLE_ID", ""),
            operator_name=os.getenv("VIHANGA_OPERATOR_NAME", ""),
            operator_password_hash=os.getenv("VIHANGA_OPERATOR_PASSWORD_HASH", ""),
            jwt_key=os.getenv("VIHANGA_JWT_KEY", ""),
            device_token_sha256=os.getenv("VIHANGA_DEVICE_TOKEN_SHA256", ""),
            allowed_hosts=tuple(x.strip() for x in
                os.getenv("VIHANGA_ALLOWED_HOSTS", "").split(",") if x.strip()),
            fc_system_id=int(os.getenv("VIHANGA_FC_SYSID", "1")),
        )
        cfg.validate()
        return cfg

    def validate(self) -> None:
        if not DEVICE_ID.fullmatch(self.vehicle_id):
            raise ValueError("VIHANGA_VEHICLE_ID must be a single explicit aircraft identifier")
        if not 1 <= self.fc_system_id <= 254:
            raise ValueError("VIHANGA_FC_SYSID must identify the exact expected flight controller")
        if not self.operator_name or not self.operator_password_hash.startswith("$argon2id$"):
            raise ValueError("Operator name and Argon2id password hash are required")
        if len(self.jwt_key.encode()) < 32:
            raise ValueError("VIHANGA_JWT_KEY must have at least 32 bytes of entropy")
        if not re.fullmatch(r"[0-9a-fA-F]{64}", self.device_token_sha256):
            raise ValueError("Device token SHA-256 must be an exact 64-hex digest")
        if not self.allowed_hosts:
            raise ValueError("Set explicit trusted private HTTPS gateway hostnames")
        if any("*" in host or "/" in host or "://" in host for host in self.allowed_hosts):
            raise ValueError("Wildcard and URL-form trusted hosts are forbidden")


def envelope(packet: bytes) -> int | None:
    """Return MAVLink message ID if the complete v1/v2 envelope is plausible."""
    if not isinstance(packet, bytes) or not 8 <= len(packet) <= MAX_FRAME:
        return None
    length = packet[1]
    if packet[0] == 0xFE and len(packet) == length + 8:
        return packet[5]
    if packet[0] == 0xFD and len(packet) >= 12:
        if packet[2] & ~0x01:
            return None
        signed = 13 if packet[2] & 1 else 0
        if len(packet) != length + 12 + signed:
            return None
        return packet[7] | packet[8] << 8 | packet[9] << 16
    return None


def valid_frame_crc(packet: bytes) -> bool:
    """Validate X25 CRC of every supported ArduPilot MAVLink v1/v2 frame.

    A plausible envelope alone is not proof of genuine flight telemetry.
    Unknown message IDs are rejected rather than passed into the native HUD.
    MAVLink2 signing is preserved but NOT authenticated by this CRC check;
    publisher authentication and a trusted physical UART remain mandatory.
    """
    message_id = envelope(packet)
    if message_id is None:
        return False
    message_class = mavlink_dialect.mavlink_map.get(message_id)
    if message_class is None:
        return False
    header_size = 6 if packet[0] == 0xFE else 10
    crc_offset = header_size + packet[1]
    crc = 0xFFFF
    for value in packet[1:crc_offset] + bytes((message_class.crc_extra,)):
        b = value ^ (crc & 0xFF)
        b ^= (b << 4) & 0xFF
        crc = ((crc >> 8) ^ (b << 8) ^ (b << 3) ^ (b >> 4)) & 0xFFFF
    return crc == packet[crc_offset] | packet[crc_offset + 1] << 8


def valid_evidence(packet: bytes, message_id: int) -> bool:
    """For critical HUD/parameter evidence, also reject truncated payloads."""
    minimum_payload = {0: 9, 22: 25, 30: 12}
    return (message_id in minimum_payload and
            envelope(packet) == message_id and
            packet[1] >= minimum_payload[message_id] and
            valid_frame_crc(packet))


@dataclass
class LinkState:
    fc_system_id: int = 1
    foreign_system_frames: int = 0
    lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    connected: bool = False
    epoch: int = 0
    viewers: set[asyncio.Queue[tuple[float, bytes]]] = field(default_factory=set)
    counters: Counter[int] = field(default_factory=Counter)
    frames: int = 0
    rejected: int = 0
    last_frame: float = -1.0
    last_heartbeat: float = -1.0
    last_attitude: float = -1.0
    parameter_frames: dict[int, tuple[float, bytes]] = field(default_factory=dict)
    parameter_expected: int = 0
    parameter_source: int = -1
    parameter_seen_at: float = -1.0

    def clear_session(self) -> None:
        self.counters.clear()
        self.frames = self.rejected = self.foreign_system_frames = 0
        self.last_frame = self.last_heartbeat = self.last_attitude = -1.0
        self.parameter_frames.clear()
        self.parameter_expected = 0
        self.parameter_source = -1
        self.parameter_seen_at = -1.0
        # No stale session packets may survive a device reconnection.
        for queue in self.viewers:
            while not queue.empty():
                queue.get_nowait()

    async def publish(self, packet: bytes) -> None:
        msg_id = envelope(packet)
        if msg_id is None or not valid_frame_crc(packet):
            self.rejected += 1
            return
        source_system = packet[3] if packet[0] == 0xFE else packet[5]
        if source_system != self.fc_system_id:
            self.rejected += 1
            self.foreign_system_frames += 1
            return
        if msg_id in (0, 22, 30) and not valid_evidence(packet, msg_id):
            self.rejected += 1
            return
        now = time.monotonic()
        self.frames += 1
        self.counters[msg_id] += 1
        self.last_frame = now
        if msg_id == 0:
            self.last_heartbeat = now
        if msg_id == 30:
            self.last_attitude = now
        if msg_id == 22:
            header = 6 if packet[0] == 0xFE else 10
            source = packet[3] if packet[0] == 0xFE else packet[5]
            component = packet[4] if packet[0] == 0xFE else packet[6]
            count, index = struct.unpack_from("<HH", packet, header + 4)
            if component == 1 and 0 < count <= 5000 and index < count:
                if count != self.parameter_expected or source != self.parameter_source:
                    self.parameter_frames.clear()
                    self.parameter_expected = count
                    self.parameter_source = source
                self.parameter_frames[index] = (now, bytes(packet))
                self.parameter_seen_at = now
        for queue in tuple(self.viewers):
            if queue.full():
                queue.get_nowait()  # Drop stale frames, never stall Pi uplink.
            queue.put_nowait((now, packet))

    def fresh(self, timestamp: float) -> bool:
        return self.connected and timestamp >= 0 and (
            time.monotonic() - timestamp) <= FRESH_SECONDS

    def parameter_status(self) -> dict[str, Any]:
        # The oldest parameter determines snapshot freshness. A single recent
        # parameter update must not revive hundreds of stale cache entries.
        oldest = min((stamp for stamp, _ in self.parameter_frames.values()),
                     default=-1.0)
        age_seconds = time.monotonic() - oldest if oldest >= 0 else None
        complete = (self.connected and self.parameter_expected > 0 and
                    len(self.parameter_frames) == self.parameter_expected and
                    age_seconds is not None and 0 <= age_seconds <= 300)
        return {
            "status": "COMPLETE" if complete else
                      "PARTIAL" if self.parameter_frames else "UNAVAILABLE",
            "verified_complete": bool(complete),
            "count": len(self.parameter_frames),
            "expected": self.parameter_expected,
            "age_ms": round(age_seconds * 1000) if age_seconds is not None else None,
            "epoch": self.epoch,
            "read_only": True,
        }

    def parameter_values(self) -> dict[str, Any]:
        status = self.parameter_status()
        if not status["verified_complete"]:
            raise ValueError("Parameter snapshot incomplete or stale")
        values = {}
        for index, (_collected, packet) in sorted(self.parameter_frames.items()):
            header = 6 if packet[0] == 0xFE else 10
            name_bytes = packet[header + 8:header + 24].split(b"\0", 1)[0]
            try:
                name = name_bytes.decode("ascii")
            except UnicodeDecodeError as ex:
                raise ValueError("Invalid FC parameter identifier") from ex
            if not name or name in values:
                raise ValueError("Duplicate or blank FC parameter identifier")
            ptype = packet[header + 24]
            value_bytes = packet[header:header + 4]
            # Avoid false drift conclusions from integer bytewise encoding.
            # Only REAL32 is safe to display as an ordinary float.
            displayed = None
            if ptype == 9:
                val = struct.unpack("<f", value_bytes)[0]
                if val == val and abs(val) != float("inf"):
                    displayed = val
            values[name] = {"index": index, "type": ptype,
                            "float32": displayed,
                            "raw_value_hex": value_bytes.hex()}
        return {"snapshot": status, "values": values,
                "writable": False, "aircraft_commands_enabled": False}

    def health(self, vehicle_id: str) -> dict[str, Any]:
        return {
            "mode": APP_MODE,
            "vehicle_id": vehicle_id,
            "device_online": self.connected,
            "fresh": self.fresh(self.last_frame),
            "native_hud_heartbeat_ready": self.fresh(self.last_heartbeat),
            "attitude_stream_active": self.fresh(self.last_attitude),
            "frames": self.frames,
            "real_heartbeat_frames": self.counters[0],
            "real_attitude_frames": self.counters[30],
            "rejected_frames": self.rejected,
            "foreign_system_frames_rejected": self.foreign_system_frames,
            "flight_controller_system_id": self.fc_system_id,
            "message_ids": {str(k): v for k, v in sorted(self.counters.items())},
            "gcs_viewers": len(self.viewers),
            "epoch": self.epoch,
            "parameter_snapshot": self.parameter_status(),
            "flight_control_enabled": False,
        }


class Login(BaseModel):
    username: str = Field(min_length=1, max_length=128)
    password: str = Field(min_length=1, max_length=512)


def create_app(config: Config | None = None) -> FastAPI:
    cfg = config or Config.from_env()
    cfg.validate()
    app = FastAPI(title="Vihanga private MAVLink relay", docs_url=None, redoc_url=None)
    app.add_middleware(TrustedHostMiddleware, allowed_hosts=list(cfg.allowed_hosts))
    state = LinkState(fc_system_id=cfg.fc_system_id)
    app.state.link = state
    ph = PasswordHasher()

    def issue_jwt() -> str:
        now = int(time.time())
        return jwt.encode({
            "sub": cfg.operator_name, "vehicle_id": cfg.vehicle_id,
            "scope": "telemetry:read", "iss": "vihanga-link",
            "aud": "vihanga-mavlink-view", "iat": now, "nbf": now,
            "exp": now + TOKEN_LIFETIME_SECONDS,
        }, cfg.jwt_key, algorithm="HS256")

    def viewer_expiration(token: str) -> int:
        try:
            claims = jwt.decode(token, cfg.jwt_key, algorithms=["HS256"],
                issuer="vihanga-link", audience="vihanga-mavlink-view",
                options={"require": ["sub", "vehicle_id", "scope", "exp", "iat", "nbf"]})
            if (claims["vehicle_id"] == cfg.vehicle_id and
                claims["scope"] == "telemetry:read" and
                claims["sub"] == cfg.operator_name):
                return int(claims["exp"])
        except (InvalidTokenError, ValueError, TypeError):
            pass
        return 0

    def bearer(ws: WebSocket) -> str:
        value = ws.headers.get("authorization", "")
        return value[7:] if value.startswith("Bearer ") else ""

    @app.get("/health")
    async def health() -> dict[str, Any]:
        return {"status": "ok", "mode": APP_MODE}

    @app.post("/api/v1/auth/login")
    async def login(body: Login) -> dict[str, Any]:
        ok = False
        if hmac.compare_digest(body.username, cfg.operator_name):
            try:
                ok = ph.verify(cfg.operator_password_hash, body.password)
            except (VerifyMismatchError, VerificationError):
                pass
        if not ok:
            raise HTTPException(status_code=401, detail="Authentication rejected")
        return {"access_token": issue_jwt(), "token_type": "bearer",
                "expires_in": TOKEN_LIFETIME_SECONDS}

    @app.get("/api/v1/mavlink/health")
    async def mavlink_health() -> dict[str, Any]:
        return state.health(cfg.vehicle_id)

    @app.get("/api/v1/mavlink/paths")
    async def paths() -> dict[str, Any]:
        return {"paths": [{"name": "cloud", "status":
            "receiving attitude" if state.fresh(state.last_attitude)
            else "heartbeat only" if state.fresh(state.last_heartbeat)
            else "offline"}]}

    @app.get("/api/v1/vehicles/{vehicle_id}/parameters")
    async def parameters(
            vehicle_id: str,
            authorization: str = Header(default="")) -> dict:
        token = authorization[7:] if authorization.startswith("Bearer ") else ""
        if not viewer_expiration(token):
            raise HTTPException(status_code=401, detail="Private operator session required")
        if vehicle_id != cfg.vehicle_id:
            raise HTTPException(status_code=404, detail="Unknown aircraft")
        return state.parameter_status()

    @app.get("/api/v1/vehicles/{vehicle_id}/parameters/values")
    async def parameter_values(
            vehicle_id: str,
            authorization: str = Header(default="")) -> dict:
        token = authorization[7:] if authorization.startswith("Bearer ") else ""
        if not viewer_expiration(token):
            raise HTTPException(status_code=401, detail="Private operator session required")
        if vehicle_id != cfg.vehicle_id:
            raise HTTPException(status_code=404, detail="Unknown aircraft")
        try:
            return state.parameter_values()
        except ValueError as ex:
            raise HTTPException(status_code=409, detail=str(ex)) from ex

    @app.post("/api/v1/sim/mission/preview")
    async def preview_mission(
            request: PreviewRequest,
            authorization: str = Header(default="")) -> dict:
        """Pure bounded planning, not mission dispatch or FC control."""
        token = (authorization[7:] if authorization.startswith("Bearer ") else "")
        if not viewer_expiration(token):
            raise HTTPException(status_code=401, detail="Private operator session required")
        if request.vehicle_id != cfg.vehicle_id:
            raise HTTPException(status_code=404, detail="Unknown aircraft")
        try:
            return preview(request)
        except ValueError as ex:
            raise HTTPException(status_code=422, detail=str(ex)) from ex

    @app.websocket("/ws/mavlink/device/{vehicle_id}")
    async def device(ws: WebSocket, vehicle_id: str) -> None:
        token = bearer(ws)
        digest = hashlib.sha256(token.encode()).hexdigest()
        if vehicle_id != cfg.vehicle_id or not hmac.compare_digest(
                digest, cfg.device_token_sha256.lower()):
            await ws.close(code=4401)
            return
        async with state.lock:
            if state.connected:
                await ws.close(code=4429)
                return
            state.connected = True
            state.epoch += 1
            state.clear_session()
        await ws.accept()
        try:
            while True:
                # A dead Pi session may not monopolize the sole device slot.
                packet = await asyncio.wait_for(ws.receive_bytes(), timeout=12)
                await state.publish(packet)
        except (WebSocketDisconnect, asyncio.TimeoutError):
            pass
        finally:
            async with state.lock:
                state.connected = False
                state.clear_session()

    @app.websocket("/ws/mavlink/view/{vehicle_id}")
    async def viewer(ws: WebSocket, vehicle_id: str) -> None:
        expiry = viewer_expiration(bearer(ws))
        if vehicle_id != cfg.vehicle_id or not expiry:
            await ws.close(code=4401)
            return
        queue: asyncio.Queue[tuple[float, bytes]] = asyncio.Queue(maxsize=32)
        async with state.lock:
            state.viewers.add(queue)
        await ws.accept()
        try:
            while time.time() < expiry:
                # Always observe disconnect and JWT expiry, including when no
                # telemetry exists. Viewer-originated bytes are NEVER routed
                # to the Pi or flight controller.
                receive = asyncio.create_task(ws.receive())
                next_frame = asyncio.create_task(queue.get())
                done, pending = await asyncio.wait(
                    {receive, next_frame}, timeout=1.0,
                    return_when=asyncio.FIRST_COMPLETED)
                for task in pending:
                    task.cancel()
                if pending:
                    await asyncio.gather(*pending, return_exceptions=True)
                if receive in done:
                    event = receive.result()
                    if event.get("type") == "websocket.disconnect":
                        break
                if next_frame in done and time.time() < expiry:
                    received_at, packet = next_frame.result()
                    if time.monotonic() - received_at <= 1.5:
                        await ws.send_bytes(packet)
        except (WebSocketDisconnect, RuntimeError, OSError):
            pass
        finally:
            async with state.lock:
                state.viewers.discard(queue)
            try:
                await ws.close(code=1000)
            except RuntimeError:
                pass

    return app


if __name__ == "__main__":
    import uvicorn
    # Bind localhost ONLY. Private HTTPS must be terminated by an approved gateway.
    uvicorn.run(create_app(), host="127.0.0.1", port=8765)
