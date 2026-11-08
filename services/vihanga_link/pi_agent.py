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
from .control_transport_probe import pi_probe_reply
from .typed_command_downlink import PiTypedCommandEngine
from .fc_readiness import PhysicalFCObserver
from .local_gpio_interlock import LocalGPIOInterlock

# pymavlink auto-detects MAVLink2 on 0xFD frames, retaining the
# correct protocol when issuing local stream requests on MAVLink1 ports.
from pymavlink import mavutil
import websockets

LOG = logging.getLogger("vihanga.edge")
MESSAGE_RATES_HZ = {30: 5, 33: 2, 74: 2, 1: 1, 24: 1, 147: 1}
MAX_FRAME = 300
MAX_CONTROL_TEXT = 4096
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
    recover_streams: bool = False

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
            recover_streams=os.getenv("VIHANGA_DISARMED_STREAM_RECOVERY", "0") == "1",
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


def require_clean_physical_uart(device: str) -> None:
    """Reject accidental MAVLink writes to the Linux login/debug console."""
    physical = os.path.realpath(device)
    serial0 = os.path.realpath("/dev/serial0")
    try:
        arguments = Path("/proc/cmdline").read_text(encoding="utf-8").split()
    except OSError as ex:
        raise RuntimeError("Cannot check Linux kernel serial console configuration") from ex
    if "systemd.debug_shell=1" in arguments:
        raise RuntimeError("Temporary passwordless systemd debug root shell still enabled")
    for entry in arguments:
        if not entry.startswith("console="):
            continue
        selected = entry.partition("=")[2].split(",", 1)[0]
        if (selected == Path(physical).name or
                selected == "serial0" and serial0 == physical):
            raise RuntimeError("Configured Linux kernel console conflicts with FC UART")
    systemctl = shutil.which("systemctl")
    if systemctl is not None:
        result = subprocess.run(
            [systemctl, "is-active", "--quiet",
             "serial-getty@" + Path(physical).name + ".service"],
            check=False, timeout=4)
        if result.returncode == 0:
            raise RuntimeError("Linux serial-getty is active on selected FC UART")


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
    if not (cfg.stream_probe or cfg.recover_streams) or not is_disarmed_autopilot_heartbeat(
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
    return ((cfg.stream_probe or cfg.recover_streams) and
            not seen_armed and not attitude_seen and
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
        self._param_refresh_pending = threading.Event()
        self._param_refresh_count = 0
        self.port = None
        self._uart_thread = None
        self._uart_failure: str | None = None
        self._loop = None
        self._lock_fd: int | None = None
        self._control_replies: asyncio.Queue[dict] = asyncio.Queue(maxsize=32)
        self._physical_monitor = PhysicalFCObserver(cfg.vehicle_id, cfg.system_id)
        self._local_safety = LocalGPIOInterlock()
        self._physical_epoch = 0
        self._physical_seq = 0
        self._last_safety_report_at = -1.0
        self._typed_control = PiTypedCommandEngine(
            cfg.vehicle_id, cfg.system_id,
            hardware_gate=self._local_safety.safety,
            fc_evidence=self._current_fc_evidence,
            deliver=self._queue_local_control_receipt)

    def _current_fc_evidence(self):
        # Called only by the UART worker, never by the cloud receiver.
        local = self._local_safety.safety()
        return self._physical_monitor.evidence(
            local, override_clear=self._local_safety.override_clear())

    def _maybe_report_safety(self) -> None:
        # Only genuine FC UART heartbeats can trigger this telemetry report.
        now = time.monotonic()
        if (self._physical_epoch < 1 or
            self._last_safety_report_at >= 0 and
            now - self._last_safety_report_at < 1.0):
            return
        self._last_safety_report_at = now
        self._physical_seq += 1
        health = self._physical_monitor.report(
            self._local_safety.safety(),
            override_clear=self._local_safety.override_clear())
        self._queue_local_control_receipt({
            "type": "fc_safety_evidence",
            "vehicle_id": self.cfg.vehicle_id,
            "control_epoch": self._physical_epoch,
            "sequence": self._physical_seq,
            "reported_at_unix_ms": int(time.time() * 1000),
            "observation": health["fields"],
            "missing_fc_messages": health["stale_or_missing"],
            "physical_control_qualified": False,
        })

    def _queue_local_control_receipt(self, message: dict) -> None:
        """Called only by the UART owner; never send from a serial thread."""
        if self._loop is None:
            return
        def add_reply() -> None:
            if self._control_replies.full():
                self._control_replies.get_nowait()
            self._control_replies.put_nowait(message)
        self._loop.call_soon_threadsafe(add_reply)

    async def _publish_control_replies(self, ws) -> None:
        """Separate bounded outbound Pi receipt pump, not a MAVLink sender."""
        while not self.stop.is_set():
            reply = await self._control_replies.get()
            await ws.send(json.dumps(reply, separators=(",", ":")))

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
            # An explicitly authorized disarmed bench probe may run even if
            # the selected messages are already streaming. Only fixed rates,
            # one active request and a small per-session quota are permitted.
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

    def queue_disarmed_parameter_request(self, message) -> dict | None:
        """Fixed read-only PARAM_REQUEST_LIST intent; no arbitrary FC writes."""
        if not isinstance(message, dict) or message.get("type") != "disarmed_param_snapshot":
            return None
        if message.get("vehicle_id") != self.cfg.vehicle_id:
            return None
        request_id = message.get("request_id")
        if not isinstance(request_id, str) or not re.fullmatch(r"[0-9a-f]{32}", request_id):
            return None
        with self._state_lock:
            if not self.cfg.parameter_snapshot:
                status = "REFUSED_DISABLED"
            elif (self._seen_armed or self._last_disarmed_heartbeat < 0 or
                  time.monotonic() - self._last_disarmed_heartbeat > 3.0):
                status = "REFUSED_DISARM_REQUIRED"
            elif self._param_refresh_pending.is_set() or self._param_refresh_count >= 1:
                status = "REFUSED_RATE_LIMIT"
            else:
                self._param_refresh_count += 1
                self._param_refresh_pending.set()
                status = "QUEUED"
        return {"type": "disarmed_param_snapshot_ack", "request_id": request_id,
                "status": status, "fc_parameters_complete": False,
                "flight_control_enabled": False}

    def transport_ping_reply(self, message) -> dict | None:
        """Reply to a matched private Pi diagnostic, with no UART activity."""
        if not isinstance(message, dict) or message.get("type") != "link_ping":
            return None
        if message.get("vehicle_id") != self.cfg.vehicle_id:
            return None
        request_id = message.get("request_id")
        if not isinstance(request_id, str) or not re.fullmatch(r"[0-9a-f]{32}", request_id):
            return None
        return {
            "type": "link_pong",
            "vehicle_id": self.cfg.vehicle_id,
            "request_id": request_id,
            "flight_control_enabled": False,
        }

    async def _receive_private_requests(self, ws) -> None:
        """Only fixed telemetry intent can be accepted from the private relay."""
        while not self.stop.is_set():
            raw = await ws.recv()
            if not isinstance(raw, str) or len(raw) > MAX_CONTROL_TEXT:
                continue
            try:
                message = json.loads(raw)
            except (ValueError, TypeError):
                continue
            pong = self.transport_ping_reply(message)
            if pong is not None:
                await ws.send(json.dumps(pong, separators=(",", ":")))
                continue
            ack = self.queue_disarmed_telemetry_request(message)
            if ack is not None:
                await ws.send(json.dumps(ack, separators=(",", ":")))
                continue
            param_ack = self.queue_disarmed_parameter_request(message)
            if param_ack is not None:
                await ws.send(json.dumps(param_ack, separators=(",", ":")))
                continue
            # Explicitly non-actuating: only echo a fixed challenge, never
            # pass an action or MAVLink payload to the UART reader.
            shadow_ack = pi_probe_reply(message, self.cfg.vehicle_id)
            if shadow_ack is not None:
                await ws.send(json.dumps(shadow_ack, separators=(",", ":")))
                continue
            if (isinstance(message, dict) and
                set(message) == {"type", "vehicle_id", "control_epoch",
                                 "physical_command_dispatch"} and
                message.get("type") == "control_session_epoch" and
                message.get("vehicle_id") == self.cfg.vehicle_id and
                message.get("physical_command_dispatch") is False and
                type(message.get("control_epoch")) is int and
                1 <= message["control_epoch"] < 2**63):
                self._typed_control.bind_session_epoch(message["control_epoch"])
                with self._state_lock:
                    self._physical_epoch = message["control_epoch"]
                    self._physical_seq = 0
                    self._last_safety_report_at = -1.0
                continue
            # Only typed ARM/DISARM intents enter the sole-owner UART worker;
            # this WSS task never calls pymavlink or writes to the Pixhawk.
            queued = self._typed_control.accept(message)
            if queued is not None:
                await ws.send(json.dumps(queued, separators=(",", ":")))

    def _read_uart(self) -> None:
        stream_probe_attempts = 0
        last_probe_at = 0.0
        attitude_seen = False
        position_seen = False
        vfr_seen = False
        parameters_requested = False
        parameter_expected = 0
        parameter_indices: set[int] = set()
        parameter_retry_indices: set[int] = set()
        parameter_capture_at = -1.0
        parameter_last_packet_at = -1.0
        parameter_last_retry_at = -1.0
        seen_armed = False
        last_heartbeat_at = -1.0
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
                # The UART owner alone can service a pending typed command.
                # Default local interlock and FC evidence providers deny it.
                self._typed_control.service_uart(self.port)
                # One bounded, read-only attempt for missing PARAM_VALUE
                # indexes. Never retry without a recent disarmed FC heartbeat.
                if (parameters_requested and parameter_expected > 0 and
                        len(parameter_indices) < parameter_expected and
                        len(parameter_retry_indices) < 32 and
                        not seen_armed and self.cfg.parameter_snapshot and
                        parameter_capture_at >= 0 and
                        0 <= now - parameter_capture_at <= 180 and
                        parameter_last_packet_at >= 0 and
                        now - parameter_last_packet_at >= 2.0 and
                        (parameter_last_retry_at < 0 or
                         now - parameter_last_retry_at >= 0.75) and
                        self._last_disarmed_heartbeat >= 0 and
                        now - self._last_disarmed_heartbeat <= 3.0):
                    missing = next(
                        (idx for idx in range(parameter_expected)
                         if idx not in parameter_indices and
                         idx not in parameter_retry_indices), None)
                    if missing is not None:
                        self.port.mav.param_request_read_send(
                            self.cfg.system_id, 1, b"\0" * 16, missing)
                        parameter_retry_indices.add(missing)
                        parameter_last_retry_at = now
                        LOG.info("Read-only missing parameter index request %d/%d",
                                 missing, parameter_expected)
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
                self._physical_monitor.observe(msg)
                if msg.get_type() == "HEARTBEAT" and msg.get_srcComponent() == 1:
                    self._maybe_report_safety()
                if msg.get_type() == "COMMAND_ACK" and msg.get_srcComponent() == 1:
                    self._typed_control.observe_uart_ack(msg)
                if (mid == 22 and msg.get_srcComponent() == 1 and
                        parameters_requested and self.cfg.parameter_snapshot):
                    count = int(msg.param_count)
                    index = int(msg.param_index)
                    if 0 < count <= 5000 and 0 <= index < count:
                        if parameter_expected != count:
                            parameter_expected = count
                            parameter_indices.clear()
                            parameter_retry_indices.clear()
                        parameter_indices.add(index)
                        parameter_last_packet_at = time.monotonic()
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
                    # A Pixhawk restart loses nonpersistent message rates.
                    # Reset only observed stream state after a real FC
                    # heartbeat gap. Never clear the lifetime armed latch.
                    heartbeat_at = time.monotonic()
                    if (last_heartbeat_at >= 0 and
                            heartbeat_at - last_heartbeat_at > 4.0):
                        attitude_seen = position_seen = vfr_seen = False
                        stream_probe_attempts = 0
                        last_probe_at = 0.0
                        parameters_requested = False
                        parameter_expected = 0
                        parameter_indices.clear()
                        parameter_retry_indices.clear()
                        parameter_capture_at = -1.0
                        parameter_last_packet_at = -1.0
                        parameter_last_retry_at = -1.0
                        if not seen_armed and is_disarmed_autopilot_heartbeat(
                                msg, self.cfg.system_id):
                            with self._state_lock:
                                self._param_refresh_count = 0
                                self._param_refresh_pending.clear()
                        LOG.warning(
                            "FC heartbeat resumed after %.1fs; invalidated volatile "
                            "stream and parameter capture state",
                            heartbeat_at - last_heartbeat_at)
                    last_heartbeat_at = heartbeat_at
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
                        if self._param_refresh_pending.is_set():
                            # Revalidate disarm immediately before UART write.
                            self._param_refresh_pending.clear()
                            parameters_requested = request_parameter_snapshot_once(
                                self.port, msg, self.cfg)
                            if parameters_requested:
                                parameter_capture_at = time.monotonic()
                                parameter_expected = 0
                                parameter_indices.clear()
                                parameter_retry_indices.clear()
                                parameter_last_packet_at = -1.0
                                parameter_last_retry_at = -1.0
                        elif self.cfg.parameter_snapshot and not parameters_requested:
                            parameters_requested = request_parameter_snapshot_once(
                                self.port, msg, self.cfg)
                            if parameters_requested:
                                parameter_capture_at = time.monotonic()
                raw = bytes(msg.get_msgbuf())
                if raw and len(raw) <= MAX_FRAME and raw[0] in (0xFE, 0xFD):
                    self._loop.call_soon_threadsafe(self._offer, raw)
            except Exception as ex:
                # Preserve the root cause for the systemd restart decision.
                # An intentional SIGTERM must not be mistaken for UART failure.
                if not self.stop.is_set():
                    self._uart_failure = f"{type(ex).__name__}: {ex}"
                    LOG.error("Physical UART reader failed: %s", self._uart_failure)
                self.stop.set()
                return

    def open_uart(self) -> None:
        require_clean_physical_uart(self.cfg.device)
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
                    self._param_refresh_pending.clear()
                    self._typed_control.start_session()
                    with self._state_lock:
                        self._physical_epoch = 0
                    while not self._control_replies.empty():
                        self._control_replies.get_nowait()
                    async with websockets.connect(
                        self.cfg.url, additional_headers={
                            "Authorization": "Bearer " + token
                        }, max_size=MAX_CONTROL_TEXT, ping_interval=15,
                        ping_timeout=10, open_timeout=12) as ws:
                        backoff = 1
                        LOG.info("Private device WSS connected; UART remains sole owned")
                        request_reader = asyncio.create_task(self._receive_private_requests(ws))
                        receipt_writer = asyncio.create_task(self._publish_control_replies(ws))
                        try:
                            await self._publish_over_ws(ws, request_reader, receipt_writer)
                        finally:
                            # An interrupted WSS session must not leave an
                            # unexecuted cloud request pending on physical UART.
                            self._remote_probe_pending.clear()
                            self._param_refresh_pending.clear()
                            self._typed_control.end_session()
                            with self._state_lock:
                                self._physical_epoch = 0
                            request_reader.cancel()
                            receipt_writer.cancel()
                            await asyncio.gather(request_reader, receipt_writer, return_exceptions=True)
                except (OSError, websockets.WebSocketException, TimeoutError) as ex:
                    LOG.warning("Private WSS disconnected (%s); retrying", type(ex).__name__)
                if not self.stop.is_set():
                    await asyncio.sleep(backoff)
                    backoff = min(15, backoff * 2)
        finally:
            self.stop.set()
            self._typed_control.end_session()
            self._local_safety.close()
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
        if self._uart_failure:
            raise RuntimeError("Physical UART reader failed: " + self._uart_failure)

    async def _publish_over_ws(self, ws, request_reader, receipt_writer) -> None:
        """Publish real frames; detect loss of the independent intent reader."""
        while not self.stop.is_set():
            if request_reader.done() or receipt_writer.done():
                raise OSError("Private authenticated control/telemetry channel disconnected")
            try:
                collected_at, frame = await asyncio.wait_for(
                    self.queue.get(), timeout=2.0)
            except asyncio.TimeoutError:
                if self.stop.is_set():
                    # SIGTERM/SIGINT closes the UART thread intentionally.
                    # A genuine reader failure propagates after cleanup.
                    return
                if self._uart_thread and not self._uart_thread.is_alive():
                    raise RuntimeError("UART reader unexpectedly exited")
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
