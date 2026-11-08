"""Typed ARM/DISARM control tunnel with Pi-local physical authority.

No raw MAVLink is accepted on any cloud endpoint. A pilot+approver lease,
FC-derived fresh evidence and exact Pi interlock are mandatory. In the current
release Pi's interlock/evidence providers default to DENY and are not
configurable through a cloud flag.
"""
from __future__ import annotations

from collections import deque
from dataclasses import dataclass
import json
import queue
import threading
import time
from typing import Any, Callable

from .control_policy import ControlDenied, FlightIntent, ObservedVehicle
from .pi_control_adapter import PiCommandAdapter, PiControlSafety


PHYSICAL_ACTIONS = frozenset({"ARM", "DISARM"})
COMMAND_FIELDS = frozenset({
    "type", "vehicle_id", "control_epoch", "expires_at_unix_ms", "intent",
})
PI_RECEIPT_FIELDS = frozenset({
    "type", "vehicle_id", "control_epoch", "request_id", "action",
    "status", "interlock", "fc_ack_received",
})
FC_RECEIPT_FIELDS = frozenset({
    "type", "vehicle_id", "control_epoch", "request_id",
    "command_id", "fc_system_id", "fc_component_id", "result",
    "fc_ack_received",
})


def outbound_command(intent: FlightIntent, vehicle_id: str, epoch: int) -> dict:
    intent.verify()
    if intent.action not in PHYSICAL_ACTIONS or intent.aircraft != vehicle_id:
        raise ControlDenied("Only explicitly approved ARM/DISARM intents are transportable")
    if type(epoch) is not int or epoch < 1:
        raise ControlDenied("Missing authenticated Pi epoch")
    return {
        "type": "typed_control_command",
        "vehicle_id": vehicle_id,
        "control_epoch": epoch,
        "expires_at_unix_ms": int(time.time() * 1000) + intent.ttl_ms,
        "intent": {
            "aircraft": intent.aircraft, "request_id": intent.request_id,
            "sequence": intent.sequence, "action": intent.action,
            "params": intent.params, "plan_sha256": intent.plan_sha256,
            "ttl_ms": intent.ttl_ms,
        },
    }


def parse_pi_command(data: Any, vehicle_id: str, *, now_ms: int | None = None) -> tuple[FlightIntent, int] | None:
    if not isinstance(data, dict) or set(data) != COMMAND_FIELDS:
        return None
    if data.get("type") != "typed_control_command" or data.get("vehicle_id") != vehicle_id:
        return None
    epoch = data.get("control_epoch")
    deadline = data.get("expires_at_unix_ms")
    if type(epoch) is not int or not 1 <= epoch < 2**63 or type(deadline) is not int:
        return None
    now_ms = int(time.time() * 1000) if now_ms is None else now_ms
    # Require close time synchronization, never extend an expired command.
    if not 0 < deadline - now_ms <= 5000:
        return None
    body = data.get("intent")
    fields = frozenset({"aircraft", "request_id", "sequence", "action",
                        "params", "plan_sha256", "ttl_ms"})
    if not isinstance(body, dict) or set(body) != fields:
        return None
    try:
        intent = FlightIntent(**body)
        intent.verify()
    except (ControlDenied, TypeError, ValueError):
        return None
    if intent.aircraft != vehicle_id or intent.action not in PHYSICAL_ACTIONS:
        return None
    return intent, epoch


class PiTypedCommandEngine:
    """UART-thread-only adapter, independent of the authenticated WSS reader.

    The default local hardware gate and observation provider both refuse
    actuation. They must be wired to physical GPIO/RC/FC sources after proper
    airframe qualification, never set by HTTP, env boolean, or a GCS packet.
    """

    def __init__(self, vehicle_id: str, system_id: int,
                 hardware_gate: Callable[[], PiControlSafety] | None = None,
                 fc_evidence: Callable[[], ObservedVehicle | None] | None = None,
                 deliver: Callable[[dict], None] | None = None):
        self.vehicle_id = vehicle_id
        self.system_id = system_id
        self.hardware_gate = hardware_gate or (lambda: PiControlSafety())
        self.fc_evidence = fc_evidence or (lambda: None)
        self.deliver = deliver or (lambda value: None)
        self.commands: queue.Queue[tuple[FlightIntent, int, int]] = queue.Queue(maxsize=1)
        self._seen: set[str] = set()
        self._seen_order: deque[str] = deque(maxlen=256)
        self._lock = threading.Lock()
        self._epoch: int | None = None
        self._session_active = False
        self._needs_abort = False
        self._adapter: PiCommandAdapter | None = None

    def start_session(self) -> None:
        with self._lock:
            self._session_active = True
            self._epoch = None
            self._needs_abort = True
            self._drain()

    def bind_session_epoch(self, epoch: int) -> None:
        """Pin the lease to an Oracle welcome from the authenticated WSS."""
        if type(epoch) is not int or not 1 <= epoch < 2**63:
            return
        with self._lock:
            if not self._session_active:
                return
            if self._epoch is not None and self._epoch != epoch:
                # An FC reboot or unexpected epoch change invalidates the
                # active session; require a fresh authenticated Pi WSS.
                self._session_active = False
                self._needs_abort = True
                self._drain()
                return
            self._epoch = epoch

    def end_session(self) -> None:
        with self._lock:
            self._session_active = False
            self._epoch = None
            self._needs_abort = True
            self._drain()
        # Adapter is only touched on the UART thread, not here. In-flight
        # state must not cause automatic retries across reconnects.

    def _drain(self) -> None:
        while True:
            try:
                self.commands.get_nowait()
            except queue.Empty:
                break

    def accept(self, data: Any) -> dict | None:
        if not isinstance(data, dict) or data.get("type") != "typed_control_command":
            return None
        parsed = parse_pi_command(data, self.vehicle_id)
        if parsed is None:
            return None
        intent, epoch = parsed
        deadline = data["expires_at_unix_ms"]
        with self._lock:
            if not self._session_active:
                return None
            if self._epoch is None or self._epoch != epoch or intent.request_id in self._seen:
                return self._refusal(intent, epoch, "PI_REFUSED_REPLAY_OR_EPOCH")
            if self.commands.full() or (self._adapter is not None and self._adapter.pending):
                return self._refusal(intent, epoch, "PI_REFUSED_BUSY")
            if len(self._seen_order) == self._seen_order.maxlen:
                self._seen.discard(self._seen_order[0])
            self._seen.add(intent.request_id)
            self._seen_order.append(intent.request_id)
            self.commands.put_nowait((intent, epoch, deadline))
        return {
            "type": "typed_control_command_queued", "vehicle_id": self.vehicle_id,
            "request_id": intent.request_id, "control_epoch": epoch,
            "flight_control_enabled": False,
        }

    def _refusal(self, intent: FlightIntent, epoch: int, status: str) -> dict:
        return {
            "type": "typed_control_pi_receipt", "vehicle_id": self.vehicle_id,
            "control_epoch": epoch, "request_id": intent.request_id,
            "action": intent.action, "status": status,
            "interlock": False, "fc_ack_received": False,
        }

    def service_uart(self, port: Any) -> None:
        """Run only on the sole UART owner thread; no async GCS UART writers."""
        with self._lock:
            if self._needs_abort:
                if self._adapter is not None:
                    self._adapter.abort()
                self._needs_abort = False
        try:
            intent, epoch, deadline = self.commands.get_nowait()
        except queue.Empty:
            return
        with self._lock:
            if not self._session_active or epoch != self._epoch:
                return
        try:
            if int(time.time() * 1000) >= deadline:
                raise ControlDenied("Expired Pi command deadline")
            gate = self.hardware_gate()
            observation = self.fc_evidence()
            if not isinstance(gate, PiControlSafety) or not isinstance(observation, ObservedVehicle):
                raise ControlDenied("Verified local interlock and physical FC evidence absent")
            gate.require()
            if self._adapter is None:
                self._adapter = PiCommandAdapter(
                    port, self.system_id, interlock=self.hardware_gate)
            response = self._adapter.send(intent, observation)
            if response.get("status") != "UART_SENT_ACK_PENDING":
                raise ControlDenied("Physical adapter did not accept command")
            message = {
                "type": "typed_control_pi_receipt", "vehicle_id": self.vehicle_id,
                "control_epoch": epoch, "request_id": intent.request_id,
                "action": intent.action, "status": "PI_UART_SENT_ACK_PENDING",
                "interlock": True, "fc_ack_received": False,
            }
        except (ControlDenied, ValueError, TypeError, OSError):
            message = self._refusal(intent, epoch, "PI_REFUSED_LOCAL_SAFETY")
        self.deliver(message)

    def observe_uart_ack(self, message: Any) -> None:
        """Only invoked on the sole UART reader after an actual FC packet."""
        if self._adapter is None or self._adapter.pending is None:
            return
        if message.get_type() != "COMMAND_ACK":
            return
        result = self._adapter.observe_command_ack(
            source_system=int(message.get_srcSystem()),
            source_component=int(message.get_srcComponent()),
            command=int(message.command), result=int(message.result))
        if result is None:
            return
        with self._lock:
            if not self._session_active or self._epoch is None:
                return
            epoch = self._epoch
        self.deliver({
            "type": "typed_control_fc_receipt", "vehicle_id": self.vehicle_id,
            "control_epoch": epoch, "request_id": result["request_id"],
            "command_id": int(message.command),
            "fc_system_id": int(message.get_srcSystem()),
            "fc_component_id": int(message.get_srcComponent()),
            "result": int(message.result), "fc_ack_received": True,
        })
