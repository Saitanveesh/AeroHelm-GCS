"""Single-owner Pi MAVLink publisher for the private Vihanga relay.

Reads genuine serial MAVLink frames and forwards the original bytes unchanged.
Optional non-persistent stream requests can be issued *locally* only after an
identified ArduPilot HEARTBEAT proves the vehicle is disarmed. This is not a
cloud flight-command bridge and never accepts command bytes from the relay.
"""
from __future__ import annotations

import asyncio
from collections import Counter
from dataclasses import dataclass
import fcntl
import hashlib
import logging
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import threading
import time
from urllib.parse import urlsplit

from pymavlink import mavutil
import websockets

LOG = logging.getLogger("vihanga.edge")
MESSAGE_RATES_HZ = {30: 5, 33: 2, 74: 2, 1: 1, 24: 1, 147: 1}
MAX_FRAME = 300
MAX_QUEUE = 256


@dataclass(frozen=True)
class Config:
    device: str
    baud: int
    vehicle_id: str
    system_id: int
    url: str
    token_file: Path
    stream_probe: bool

    @staticmethod
    def from_env() -> "Config":
        cfg = Config(
            device=os.getenv("VIHANGA_PI_SERIAL", ""),
            baud=int(os.getenv("VIHANGA_PI_BAUD", "57600")),
            vehicle_id=os.getenv("VIHANGA_VEHICLE_ID", ""),
            system_id=int(os.getenv("VIHANGA_FC_SYSID", "1")),
            url=os.getenv("VIHANGA_DEVICE_WS_URL", ""),
            token_file=Path(os.getenv("VIHANGA_DEVICE_TOKEN_FILE", "")),
            stream_probe=os.getenv("VIHANGA_DISARMED_STREAM_PROBE", "0") == "1",
        )
        cfg.validate()
        return cfg

    def validate(self) -> None:
        if not self.device.startswith("/dev/") or ".." in self.device:
            raise ValueError("VIHANGA_PI_SERIAL must be a real /dev serial device")
        if not 9600 <= self.baud <= 921600:
            raise ValueError("Invalid serial baud")
        if not re.fullmatch(r"[A-Za-z0-9_-]{3,64}", self.vehicle_id):
            raise ValueError("Invalid aircraft identifier")
        if not 1 <= self.system_id <= 254:
            raise ValueError("FC system ID must be explicit and nonzero")
        url = urlsplit(self.url)
        if (url.scheme != "wss" or not url.hostname or url.username or
            url.password or url.query or url.fragment or
            url.path != "/ws/mavlink/device/" + self.vehicle_id or
            not url.hostname.endswith(".ts.net")):
            raise ValueError("Device uplink must use exact private Tailscale WSS URL")


def token_from_file(path: Path) -> str:
    if not path.is_file() or (path.stat().st_mode & 0o077):
        raise PermissionError("Pi device token must be a local 0600 file")
    value = path.read_text(encoding="utf-8").strip()
    if len(value) < 32:
        raise ValueError("Pi device token must contain at least 32 random characters")
    return value


def valid_uart_owner_probe(port: str) -> None:
    """Refuse to start while another process owns the serial device."""
    binary = shutil.which("fuser")
    if binary is None:
        raise RuntimeError("fuser required; cannot establish exclusive UART ownership")
    result = subprocess.run([binary, "-s", port], check=False, timeout=4)
    if result.returncode == 0:
        raise RuntimeError("Serial device already open. Stop existing UART owner explicitly")
    if result.returncode != 1:
        raise RuntimeError("Cannot determine serial device ownership")
    # The new process also holds a cooperative flock to stop duplicate agents.


def is_disarmed_autopilot_heartbeat(message, sysid: int) -> bool:
    if message.get_type() != "HEARTBEAT":
        return False
    if message.get_srcSystem() != sysid or message.get_srcComponent() != 1:
        return False
    if message.autopilot != mavutil.mavlink.MAV_AUTOPILOT_ARDUPILOTMEGA:
        return False
    return not bool(message.base_mode & mavutil.mavlink.MAV_MODE_FLAG_SAFETY_ARMED)


def request_streams_once(port, heartbeat, cfg: Config) -> bool:
    """Request volatile message intervals on the local Pi UART, never via cloud."""
    if not cfg.stream_probe or not is_disarmed_autopilot_heartbeat(
            heartbeat, cfg.system_id):
        return False
    for msg_id, rate in MESSAGE_RATES_HZ.items():
        port.mav.command_long_send(
            cfg.system_id, 1,
            mavutil.mavlink.MAV_CMD_SET_MESSAGE_INTERVAL, 0,
            float(msg_id), float(round(1_000_000 / rate)),
            0, 0, 0, 0, 0)
        time.sleep(0.10)
    LOG.warning("Sent disarmed-only VOLATILE stream requests; verify actual ID counts")
    return True


class Publisher:
    def __init__(self, cfg: Config):
        self.cfg = cfg
        self.queue: asyncio.Queue[bytes] = asyncio.Queue(maxsize=MAX_QUEUE)
        self.stop = threading.Event()
        self.counts: Counter[int] = Counter()
        self.dropped = 0
        self.port = None
        self._uart_thread = None
        self._loop = None

    def _offer(self, raw: bytes) -> None:
        if self.queue.full():
            self.queue.get_nowait()
            self.dropped += 1
        self.queue.put_nowait(raw)

    def _read_uart(self) -> None:
        probed = False
        seen_armed = False
        while not self.stop.is_set():
            try:
                msg = self.port.recv_match(blocking=True, timeout=0.4)
                if msg is None or msg.get_type() == "BAD_DATA":
                    continue
                mid = int(msg.get_msgId())
                if mid < 0:
                    continue
                self.counts[mid] += 1
                if msg.get_type() == "HEARTBEAT" and \
                        msg.get_srcSystem() == self.cfg.system_id and \
                        msg.get_srcComponent() == 1:
                    armed = bool(msg.base_mode &
                        mavutil.mavlink.MAV_MODE_FLAG_SAFETY_ARMED)
                    seen_armed = seen_armed or armed
                    # Never start stream reconfiguration if this session has
                    # EVER observed an armed FC. An onboard operator must
                    # explicitly restart a disarmed maintenance session.
                    if self.cfg.stream_probe and not probed and not seen_armed:
                        probed = request_streams_once(self.port, msg, self.cfg)
                raw = bytes(msg.get_msgbuf())
                if raw and len(raw) <= MAX_FRAME and raw[0] in (0xFE, 0xFD):
                    self._loop.call_soon_threadsafe(self._offer, raw)
            except Exception as ex:
                LOG.error("UART reader stopped: %s", type(ex).__name__)
                self.stop.set()
                return

    def open_uart(self) -> None:
        valid_uart_owner_probe(self.cfg.device)
        self.port = mavutil.mavlink_connection(
            self.cfg.device, baud=self.cfg.baud, autoreconnect=False,
            source_system=255, source_component=190, dialect="ardupilotmega")
        self._loop = asyncio.get_running_loop()
        self._uart_thread = threading.Thread(
            target=self._read_uart, name="vihanga-sole-uart-reader", daemon=True)
        self._uart_thread.start()

    async def run(self) -> None:
        token = token_from_file(self.cfg.token_file)
        self.open_uart()
        backoff = 1
        try:
            while not self.stop.is_set():
                try:
                    # After an outage, do not replay buffered old telemetry.
                    while not self.queue.empty():
                        self.queue.get_nowait()
                    async with websockets.connect(
                        self.cfg.url, additional_headers={
                            "Authorization": "Bearer " + token
                        }, max_size=MAX_FRAME, ping_interval=15,
                        ping_timeout=10, open_timeout=12) as ws:
                        backoff = 1
                        LOG.info("Private device WSS connected; UART remains sole owned")
                        while not self.stop.is_set():
                            try:
                                frame = await asyncio.wait_for(
                                    self.queue.get(), timeout=2.0)
                            except asyncio.TimeoutError:
                                if self._uart_thread and not self._uart_thread.is_alive():
                                    raise RuntimeError("UART reader terminated")
                                continue
                            await ws.send(frame)
                except (OSError, websockets.WebSocketException, TimeoutError) as ex:
                    LOG.warning("Private WSS disconnected (%s); retrying", type(ex).__name__)
                if not self.stop.is_set():
                    await asyncio.sleep(backoff)
                    backoff = min(15, backoff * 2)
        finally:
            self.stop.set()
            if self.port is not None:
                self.port.close()
            if self._uart_thread is not None:
                self._uart_thread.join(timeout=2)
            LOG.info("UART publisher stopped; frames %s; queue drops %d",
                dict(self.counts), self.dropped)


async def main() -> None:
    logging.basicConfig(level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s")
    cfg = Config.from_env()
    publisher = Publisher(cfg)
    for sig in (signal.SIGTERM, signal.SIGINT):
        try:
            asyncio.get_running_loop().add_signal_handler(sig, publisher.stop.set)
        except NotImplementedError:
            pass
    await publisher.run()


if __name__ == "__main__":
    asyncio.run(main())
