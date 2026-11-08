"""Isolated, guarded PARAM_SET/PARAM_VALUE microservice for future Pi integration.

No automatic retries, unsafe configuration keys, or mutation of the production
relay. The Pi hardware switch defaults to off, and FC readback must match.
"""
from __future__ import annotations

import math
import time
from typing import Any, Callable

from .control_policy import ControlDenied, FlightIntent, ObservedVehicle
from .pi_control_adapter import PiControlSafety


# MAV_PARAM_TYPE_REAL32. Other MAV_PARAM_TYPEs require verified autopilot-
# specific conversions, not assumed float and not raw byte reinterpreting.
MAV_PARAM_TYPE_REAL32 = 9


class ParameterWriteTransaction:
    def __init__(self, port: Any, *, system_id: int,
                 interlock: Callable[[], PiControlSafety] | None = None,
                 clock: Callable[[], float] = time.monotonic):
        if not 1 <= system_id <= 254:
            raise ControlDenied("Explicit FC SYSID required")
        self.port = port
        self.system_id = system_id
        self.interlock = interlock or (lambda: PiControlSafety())
        self.clock = clock
        self.pending: tuple[str, str, float, float] | None = None
        self.status = "IDLE"

    def _gate(self) -> None:
        evidence = self.interlock()
        if not isinstance(evidence, PiControlSafety):
            raise ControlDenied("Invalid independent Pi-local safety interlock")
        evidence.require()

    def send(self, intent: FlightIntent, evidence: ObservedVehicle,
             observed_type: int) -> dict:
        intent.verify()
        if intent.action != "PARAM_SET":
            raise ControlDenied("Not a typed PARAM_SET transaction")
        self._gate()
        if self.pending is not None:
            raise ControlDenied("Other parameter write awaiting physical readback")
        evidence.require_current(intent.aircraft, self.system_id, navigation=False)
        if evidence.armed or not evidence.on_ground:
            raise ControlDenied("Physical parameter changes require grounded disarmed FC")
        if observed_type != MAV_PARAM_TYPE_REAL32:
            raise ControlDenied("Only physically observed REAL32 parameter type supported")
        param_id = intent.params["name"]
        value = float(intent.params["value"])
        # Use the original physical FC parameter type rather than coercing
        # safety-critical integer and bitmask values.
        self.port.mav.param_set_send(
            self.system_id, 1, param_id.encode("ascii").ljust(16, b"\0"),
            value, MAV_PARAM_TYPE_REAL32)
        self.pending = intent.request_id, param_id, value, self.clock() + 4.0
        self.status = "PARAM_SET_SENT_AWAITING_FC_VALUE"
        return {"request_id": intent.request_id, "status": self.status,
                "verified_readback": False, "parameter_write_completed": False}

    def observe_value(self, *, source_system: int, source_component: int,
                      name: str, value: float, parameter_type: int) -> dict | None:
        if self.pending is None:
            return None
        request_id, expected_name, expected_value, deadline = self.pending
        if self.clock() >= deadline:
            self.status = "TIMEOUT_NO_REPLAY"
            self.pending = None
            return {"request_id": request_id, "status": self.status,
                    "verified_readback": False, "parameter_write_completed": False}
        if (source_system != self.system_id or source_component != 1 or
            name != expected_name or parameter_type != MAV_PARAM_TYPE_REAL32 or
            type(value) not in (float, int) or not math.isfinite(value)):
            return None
        close = math.isclose(value, expected_value, rel_tol=1e-6, abs_tol=1e-4)
        self.status = ("FC_PARAM_VALUE_VERIFIED" if close
                       else "FC_PARAM_VALUE_MISMATCH")
        self.pending = None
        return {"request_id": request_id, "status": self.status,
                "verified_readback": close, "parameter_write_completed": close,
                "name": expected_name,
                "observed_value": value}

    def abort(self) -> None:
        # Never blindly roll back parameters, especially across an FC reboot.
        # Read the current setting and reconcile under fresh authorization.
        self.pending = None
        self.status = "ABORTED_RECONCILE_READBACK"
