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
import hmac
import json
import logging
import os
from pathlib import Path
import re
import shutil
import signal
import tempfile
import subprocess
import threading
import time
from urllib.parse import urlsplit
from urllib.request import Request, urlopen
from .oracle_identity import validate_instance_ocid

# pymavlink auto-detects MAVLink2 on 0xFD frames, retaining the
# correct protocol when issuing local stream requests on MAVLink1 ports.
from pymavlink import mavutil
import websockets

LOG = logging.getLogger("vihanga.edge")
MESSAGE_RATES_HZ = {30: 5, 33: 2, 74: 2, 1: 1, 24: 1, 147: 1}
MAX_FRAME = 300
MAX_QUEUE = 256
OBSERVATION_IDS = (0, 30, 33, 74, 24, 1, 147)
STREAM_PROBE_MAX_ATTEMPTS = 2
STREAM_PROBE_RETRY_SECONDS = 15.0


def observation_delta(current: Counter[int], previous: Counter[int]) -> dict[int, int]:
    """Per-ID observed real packet counts, with no inferred attitude frames."""
    return {mid: max(0, current[mid] - previous[mid]) for mid in OBSERVATION_IDS}


@dataclass(frozen=True)
class Config:
    device: str
    baud: int
    vehicle_id: str
    system_id: int
    url: str
    token_file: Path
    stream_probe: bool
    pinned_relay_host: str
    parameter_snapshot: bool = False
    oracle_instance_ocid: str = ""

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
            pinned_relay_host=os.getenv("VIHANGA_PINNED_RELAY_HOST", ""),
            parameter_snapshot=os.getenv("VIHANGA_DISARMED_PARAM_SNAPSHOT", "0") == "1",
            oracle_instance_ocid=os.getenv("VIHANGA_ORACLE_INSTANCE_OCID", ""),
        )
        cfg.validate()
        return cfg

    def validate(self) -> None:
        if self.oracle_instance_ocid:
            validate_instance_ocid(self.oracle_instance_ocid)
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
            not url.hostname.endswith(".ts.net") or
            not self.pinned_relay_host or
            url.hostname != self.pinned_relay_host.lower()):
            raise ValueError("Device uplink requires a pinned private relay hostname and WSS URL")


def verify_remote_oracle_instance(cfg: Config, fetch=None) -> bool:
    """Pin Pi WSS destination to the expected OCI VM and TLS hostname.

    Oracle instance OCID does not replace the pinned Tailscale TLS identity.
    Verify before the device bearer token is ever transmitted.
    """
    if not cfg.oracle_instance_ocid:
        return False
    cfg.validate()
    expected = hashlib.sha256(cfg.oracle_instance_ocid.encode("ascii")).hexdigest()
    base = urlsplit(cfg.url)
    # Preserve the HTTPS port: the new Vihanga relay may run on :8443
    # while the legacy AeroHelm API continues unchanged on :443.
    target = "https://" + base.netloc + "/health"
    if fetch is None:
        def fetch():
            request = Request(target, headers={"Accept": "application/json"})
            with urlopen(request, timeout=5.0) as response:
                if response.geturl() != target:
                    raise RuntimeError("Relay health identity redirect forbidden")
                content = response.read(8193)
                if len(content) > 8192:
                    raise RuntimeError("Relay health response too large")
                return json.loads(content)
    try:
        report = fetch()
    except Exception as ex:
        raise RuntimeError("Cannot verify Oracle-bound private relay") from ex
    if not isinstance(report, dict) or report.get("service") != "vihanga-private-relay":
        raise RuntimeError("Endpoint is not the independently deployed Vihanga relay")
    binding = report.get("oracle_instance", {})
    if (not isinstance(binding, dict) or binding.get("configured") is not True or
        binding.get("verified") is not True or
        not isinstance(binding.get("instance_sha256"), str) or
        not hmac.compare_digest(binding["instance_sha256"], expected)):
        raise RuntimeError("Private relay is not bound to the expected Oracle instance")
    return True


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



def stream_probe_due(cfg: Config, *, seen_armed: bool, attitude_seen: bool,
                     attempts: int, seconds_since_last_probe: float) -> bool:
    """Permit at most one disarmed/local retry when ID30 never arrives."""
    return (cfg.stream_probe and not seen_armed and not attitude_seen and
            attempts < STREAM_PROBE_MAX_ATTEMPTS and
            (attempts == 0 or seconds_since_last_probe >= STREAM_PROBE_RETRY_SECONDS))

def request_parameter_snapshot_once(port, heartbeat, cfg: Config) -> bool:
    """One local read-only FC parameter-list request on the disarmed bench."""
    if not cfg.parameter_snapshot or not is_disarmed_autopilot_heartbeat(
            heartbeat, cfg.system_id):
        return False
    port.mav.param_request_list_send(cfg.system_id, 1)
    LOG.info("Requested a disarmed-only PARAM_VALUE snapshot on local Pi UART")
    return True


class Publisher:
    def __init__(self, cfg: Config):
        self.cfg = cfg
        self.queue: asyncio.Queue[tuple[float, bytes]] = asyncio.Queue(maxsize=MAX_QUEUE)
        self.stop = threading.Event()
        self.counts: Counter[int] = Counter()
        self.sent_counts: Counter[int] = Counter()
        self.foreign_system_dropped = 0
        self.dropped = 0
        self._state_lock = threading.Lock()
        self._seen_armed = False
        self._last_disarmed_heartbeat = -1.0
        self._attitude_seen = False
        self._position_seen = False
        self._vfr_seen = False
        self._remote_probe_count = 0
        self._remote_probe_requested_at = -1.0
        self._remote_probe_pending = threading.Event()
        self.port = None
        self._uart_thread = None
        self._loop = None
        self._lock_fd: int | None = None

    def _offer(self, raw: bytes) -> None:
        if self.queue.full():
            self.queue.get_nowait()
            self.dropped += 1
        self.queue.put_nowait((time.monotonic(), raw))

    def queue_disarmed_telemetry_request(self, message) -> dict | None:
        """Translate a single fixed cloud intent into a local UART bench probe.

        No raw MAVLink/command/rate originates from Windows or the relay.
        An identified, fresh disarmed FC heartbeat is mandatory, with a
        permanent armed-observed latch for this agent lifetime.
        """
        if not isinstance(message, dict) or message.get("type") != "disarmed_stream_probe":
            return None
        if message.get("vehicle_id") != self.cfg.vehicle_id:
            return None
        request_id = message.get("request_id")
        if not isinstance(request_id, str) or not re.fullmatch(r"[0-9a-f]{32}", request_id):
            return None
        status = "QUEUED"
        with self._state_lock:
            now = time.monotonic()
            if not self.cfg.stream_probe:
                status = "REFUSED_DISABLED"
            elif self._seen_armed or self._last_disarmed_heartbeat < 0 or \
                    now - self._last_disarmed_heartbeat > 3.0:
                status = "REFUSED_DISARM_REQUIRED"
            elif self._attitude_seen and (self._position_seen or self._vfr_seen):
                status = "REFUSED_ALREADY_STREAMING"
            elif self._remote_probe_pending.is_set() or self._remote_probe_count >= 2 or (
                    self._remote_probe_requested_at >= 0 and
                    now - self._remote_probe_requested_at < 15.0):
                status = "REFUSED_RATE_LIMIT"
            else:
                self._remote_probe_count += 1
                self._remote_probe_requested_at = now
                self._remote_probe_pending.set()
        return {"type": "disarmed_stream_probe_ack", "request_id": request_id,
                "status": status, "fc_stream_confirmed": False,
                "flight_control_enabled": False}

    async def _receive_private_requests(self, ws) -> None:
        """Only fixed telemetry intent can be accepted from the private relay."""
        while not self.stop.is_set():
            raw = await ws.recv()
            if not isinstance(raw, str) or len(raw) > 300:
                continue
            try:
                message = json.loads(raw)
            except (ValueError, TypeError):
                continue
            ack = self.queue_disarmed_telemetry_request(message)
            if ack is not None:
                await ws.send(json.dumps(ack, separators=(",", ":")))

    def _read_uart(self) -> None:
        stream_probe_attempts = 0
        last_probe_at = 0.0
        attitude_seen = False
        position_seen = False
        vfr_seen = False
        parameters_requested = False
        seen_armed = False
        last_report = time.monotonic()
        previous_uart: Counter[int] = Counter()
        previous_uplink: Counter[int] = Counter()
        while not self.stop.is_set():
            try:
                now = time.monotonic()
                if now - last_report >= 10.0:
                    observed = observation_delta(self.counts, previous_uart)
                    sent = observation_delta(self.sent_counts, previous_uplink)
                    LOG.info(
                        "10s physical UART IDs HB=%d ATT=%d POS=%d HUD=%d GPS=%d; "
                        "cloud WSS sent HB=%d ATT=%d; queue drops=%d; foreign SYSID drops=%d",
                        observed[0], observed[30], observed[33], observed[74],
                        observed[24], sent[0], sent[30], self.dropped,
                        self.foreign_system_dropped)
                    if observed[0] > 0 and observed[30] == 0:
                        LOG.warning(
                            "Real FC HEARTBEAT present but no ATTITUDE ID30 on Pi UART; "
                            "inspect FC stream source/configuration on disarmed bench")
                    previous_uart = self.counts.copy()
                    previous_uplink = self.sent_counts.copy()
                    last_report = now
                msg = self.port.recv_match(blocking=True, timeout=0.4)
                if msg is None or msg.get_type() == "BAD_DATA":
                    continue
                mid = int(msg.get_msgId())
                if mid < 0:
                    continue
                # A single-aircraft credential may never relay another SYSID.
                # Keep the rejected count separate from observed FC telemetry.
                if msg.get_srcSystem() != self.cfg.system_id:
                    self.foreign_system_dropped += 1
                    continue
                self.counts[mid] += 1
                if mid == 77 and msg.get_srcComponent() == 1 and \
                        int(msg.command) == mavutil.mavlink.MAV_CMD_SET_MESSAGE_INTERVAL:
                    LOG.info("REAL FC COMMAND_ACK for stream interval: MAV_RESULT=%d",
                             int(msg.result))
                if mid == 30:
                    attitude_seen = True
                    with self._state_lock:
                        self._attitude_seen = True
                if mid == 33:
                    position_seen = True
                    with self._state_lock:
                        self._position_seen = True
                if mid == 74:
                    vfr_seen = True
                    with self._state_lock:
                        self._vfr_seen = True
                if msg.get_type() == "HEARTBEAT" and \
                        msg.get_srcSystem() == self.cfg.system_id and \
                        msg.get_srcComponent() == 1:
                    armed = bool(msg.base_mode &
                        mavutil.mavlink.MAV_MODE_FLAG_SAFETY_ARMED)
                    seen_armed = seen_armed or armed
                    with self._state_lock:
                        self._seen_armed = self._seen_armed or armed
                        if is_disarmed_autopilot_heartbeat(msg, self.cfg.system_id) and \
                                not self._seen_armed:
                            self._last_disarmed_heartbeat = time.monotonic()
                    # Never start stream reconfiguration if this session has
                    # EVER observed an armed FC. An onboard operator must
                    # explicitly restart a disarmed maintenance session.
                    if not seen_armed:
                        remote_issued = False
                        if self._remote_probe_pending.is_set():
                            self._remote_probe_pending.clear()
                            if not (attitude_seen and (position_seen or vfr_seen)) and \
                                    self.cfg.stream_probe:
                                remote_issued = request_streams_once(
                                    self.port, msg, self.cfg)
                        if not remote_issued and stream_probe_due(
                                self.cfg, seen_armed=seen_armed,
                                # A native Quick feed requires genuine attitude
                                # plus ID33 position or ID74 VFR_HUD.
                                attitude_seen=(attitude_seen and (position_seen or vfr_seen)),
                                attempts=stream_probe_attempts,
                                seconds_since_last_probe=time.monotonic() - last_probe_at):
                            if request_streams_once(self.port, msg, self.cfg):
                                stream_probe_attempts += 1
                                last_probe_at = time.monotonic()
                                LOG.info("Disarmed local stream probe attempt %d/%d",
                                         stream_probe_attempts, STREAM_PROBE_MAX_ATTEMPTS)
                        if self.cfg.parameter_snapshot and not parameters_requested:
                            parameters_requested = request_parameter_snapshot_once(
                                self.port, msg, self.cfg)
                raw = bytes(msg.get_msgbuf())
                if raw and len(raw) <= MAX_FRAME and raw[0] in (0xFE, 0xFD):
                    self._loop.call_soon_threadsafe(self._offer, raw)
            except Exception as ex:
                LOG.error("UART reader stopped: %s", type(ex).__name__)
                self.stop.set()
                return

    def open_uart(self) -> None:
        # Cooperative lock closes the fuser/open race between two instances.
        name = hashlib.sha256(os.path.realpath(self.cfg.device).encode()).hexdigest()[:20]
        lock_path = os.path.join(tempfile.gettempdir(), "vihanga-uart-" + name + ".lock")
        flags = os.O_RDWR | os.O_CREAT
        if hasattr(os, "O_NOFOLLOW"):
            flags |= os.O_NOFOLLOW
        fd = os.open(lock_path, flags, 0o600)
        try:
            if os.fstat(fd).st_uid != os.getuid():
                raise PermissionError("UART lockfile belongs to another user")
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            valid_uart_owner_probe(self.cfg.device)
            self.port = mavutil.mavlink_connection(
                self.cfg.device, baud=self.cfg.baud, autoreconnect=False,
                source_system=255, source_component=190, dialect="ardupilotmega")
            self._lock_fd = fd
        except BaseException:
            os.close(fd)
            raise
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
                    await asyncio.to_thread(verify_remote_oracle_instance, self.cfg)
                    # After an outage, do not replay buffered old telemetry.
                    while not self.queue.empty():
                        self.queue.get_nowait()
                    self._remote_probe_pending.clear()
                    async with websockets.connect(
                        self.cfg.url, additional_headers={
                            "Authorization": "Bearer " + token
                        }, max_size=MAX_FRAME, ping_interval=15,
                        ping_timeout=10, open_timeout=12) as ws:
                        backoff = 1
                        LOG.info("Private device WSS connected; UART remains sole owned")
                        request_reader = asyncio.create_task(self._receive_private_requests(ws))
                        try:
                            await self._publish_over_ws(ws, request_reader)
                        finally:
                            # An interrupted WSS session must not leave an
                            # unexecuted cloud request pending on physical UART.
                            self._remote_probe_pending.clear()
                            request_reader.cancel()
                            await asyncio.gather(request_reader, return_exceptions=True)
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
            if self._lock_fd is not None:
                fcntl.flock(self._lock_fd, fcntl.LOCK_UN)
                os.close(self._lock_fd)
                self._lock_fd = None
            LOG.info(
                "UART publisher stopped; observed %s; sent %s; queue drops %d; foreign SYSID drops %d",
                dict(self.counts), dict(self.sent_counts), self.dropped,
                self.foreign_system_dropped)

    async def _publish_over_ws(self, ws, request_reader) -> None:
        """Publish real frames; detect loss of the independent intent reader."""
        while not self.stop.is_set():
            if request_reader.done():
                raise OSError("Private telemetry request channel disconnected")
            try:
                collected_at, frame = await asyncio.wait_for(
                    self.queue.get(), timeout=2.0)
            except asyncio.TimeoutError:
                if self._uart_thread and not self._uart_thread.is_alive():
                    raise RuntimeError("UART reader terminated")
                continue
            if time.monotonic() - collected_at <= 1.5:
                await ws.send(frame)
                mid = (frame[5] if frame[0] == 0xFE else
                       frame[7] | frame[8] << 8 | frame[9] << 16)
                self.sent_counts[mid] += 1
            else:
                self.dropped += 1


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
