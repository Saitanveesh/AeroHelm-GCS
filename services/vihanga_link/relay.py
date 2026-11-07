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
import time
from typing import Any

from argon2 import PasswordHasher
from argon2.exceptions import VerifyMismatchError, VerificationError
from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse
from fastapi.security import HTTPBearer
import jwt
from jwt import InvalidTokenError
from pydantic import BaseModel, Field
from starlette.middleware.trustedhost import TrustedHostMiddleware

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
        )
        cfg.validate()
        return cfg

    def validate(self) -> None:
        if not DEVICE_ID.fullmatch(self.vehicle_id):
            raise ValueError("VIHANGA_VEHICLE_ID must be a single explicit aircraft identifier")
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


def valid_evidence(packet: bytes, message_id: int) -> bool:
    """Independent MAVLink X25+extra CRC for heartbeat and attitude evidence."""
    if message_id not in (0, 30) or envelope(packet) != message_id:
        return False
    if packet[1] < (9 if message_id == 0 else 12):
        return False
    header = 6 if packet[0] == 0xFE else 10
    idx = header + packet[1]
    crc = 0xFFFF
    for value in packet[1:idx] + bytes((50 if message_id == 0 else 39,)):
        b = value ^ (crc & 0xFF)
        b ^= (b << 4) & 0xFF
        crc = ((crc >> 8) ^ (b << 8) ^ (b << 3) ^ (b >> 4)) & 0xFFFF
    return crc == packet[idx] | packet[idx + 1] << 8


@dataclass
class LinkState:
    lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    connected: bool = False
    epoch: int = 0
    viewers: set[asyncio.Queue[bytes]] = field(default_factory=set)
    counters: Counter[int] = field(default_factory=Counter)
    frames: int = 0
    rejected: int = 0
    last_frame: float = -1.0
    last_heartbeat: float = -1.0
    last_attitude: float = -1.0

    def clear_session(self) -> None:
        self.counters.clear()
        self.frames = self.rejected = 0
        self.last_frame = self.last_heartbeat = self.last_attitude = -1.0
        # No stale session packets may survive a device reconnection.
        for queue in self.viewers:
            while not queue.empty():
                queue.get_nowait()

    async def publish(self, packet: bytes) -> None:
        msg_id = envelope(packet)
        if msg_id is None:
            self.rejected += 1
            return
        if msg_id in (0, 30) and not valid_evidence(packet, msg_id):
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
        for queue in tuple(self.viewers):
            if queue.full():
                queue.get_nowait()  # Drop stale frames, never stall Pi uplink.
            queue.put_nowait(packet)

    def fresh(self, timestamp: float) -> bool:
        return self.connected and timestamp >= 0 and (
            time.monotonic() - timestamp) <= FRESH_SECONDS

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
            "message_ids": {str(k): v for k, v in sorted(self.counters.items())},
            "gcs_viewers": len(self.viewers),
            "epoch": self.epoch,
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
    state = LinkState()
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
        queue: asyncio.Queue[bytes] = asyncio.Queue(maxsize=64)
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
                    await ws.send_bytes(next_frame.result())
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
