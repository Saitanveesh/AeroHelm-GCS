"""Isolated Pixhawk command encoder, not connected to production Pi WSS.

Physical FC commands are possible only after an independent local hardware
interlock, current FC-owned safety evidence, and an authorized pilot lease are
provided by the future reviewed dispatcher. There is NO built-in enable
mechanism: the default interlock always fails closed.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Callable, Any

from .control_policy import (
    ControlDenied, FlightIntent, ObservedVehicle, NAV_ACTIONS,
    PREPARE_ACTIONS, PARAMETER_ALLOWLIST,
)


CMD_ARM_DISARM = 400
CMD_TAKEOFF = 22
CMD_LAND = 21
CMD_RTL = 20
CMD_SET_MODE = 176
CMD_MISSION_START = 300
CUSTOM_MODE_FLAG = 1
# Firmware-specific mappings must be independently verified against the
# attached ArduCopter firmware before these values are used for flight.
COPTER_MODES = {
    "STABILIZE": 0, "ALT_HOLD": 2, "AUTO": 3, "GUIDED": 4,
    "LOITER": 5, "RTL": 6, "LAND": 9, "BRAKE": 17,
}


@dataclass(frozen=True)
class PiControlSafety:
    hardware_interlock_active: bool = False
    local_pilot_present: bool = False
    independent_rc_available: bool = False
    adapter_authorized: bool = False
    firmware_mapping_verified: bool = False

    def require(self) -> None:
        if not (self.hardware_interlock_active and self.local_pilot_present and
                self.independent_rc_available and self.adapter_authorized):
            raise ControlDenied("Physical on-site interlock, independent RC and Pi authority required")


class PiCommandAdapter:
    """One pending COMMAND_LONG at a time, no implicit retries.

    The class does not open UARTs: the caller passes the *sole existing*
    pymavlink connection. Input must be produced by the validated remote
    authority broker and rechecked against real Pi MAVLink evidence locally.
    """

    def __init__(self, port: Any, system_id: int,
                 interlock: Callable[[], PiControlSafety] | None = None):
        if not 1 <= system_id <= 254:
            raise ControlDenied("Exact FC system ID required")
        self.port = port
        self.system_id = system_id
        self.interlock = interlock or (lambda: PiControlSafety())
        self.pending: tuple[str, int] | None = None

    def send(self, intent: FlightIntent, evidence: ObservedVehicle) -> dict:
        intent.verify()
        gate = self.interlock()
        if not isinstance(gate, PiControlSafety):
            raise ControlDenied("Invalid local hardware interlock result")
        gate.require()
        if self.pending is not None:
            raise ControlDenied("Physical command outstanding; cannot issue another")
        evidence.require_current(intent.aircraft, self.system_id,
                                 navigation=intent.action in NAV_ACTIONS)
        if intent.action in PREPARE_ACTIONS and (evidence.armed or
                                                not evidence.on_ground):
            raise ControlDenied("Write or mission preparation requires landed, disarmed FC")
        if intent.action == "ARM" and (evidence.armed or not evidence.on_ground):
            raise ControlDenied("ARM requires landed disarmed FC")
        if intent.action == "DISARM" and (not evidence.armed or not evidence.on_ground):
            raise ControlDenied("In-flight disarm prohibited")
        if intent.action == "TAKEOFF" and (not evidence.armed or
                                         evidence.mode != "GUIDED"):
            raise ControlDenied("TAKEOFF requires armed GUIDED mode")
        if intent.action == "MISSION_START" and (not evidence.armed or
                                                evidence.mode != "AUTO"):
            raise ControlDenied("Mission start requires armed AUTO mode")
        if intent.action == "RTL" and not evidence.armed:
            raise ControlDenied("RTL requires armed FC")
        if (intent.action == "SET_MODE" and
                intent.params["mode"] in ("AUTO", "GUIDED", "LOITER", "RTL") and
                (not evidence.home_verified or evidence.gps_fix is None or
                 evidence.gps_fix < 3)):
            raise ControlDenied("Navigation mode requires verified GPS and home")
        if intent.action == "SET_MODE" and not gate.firmware_mapping_verified:
            raise ControlDenied("FC mode map not verified against installed firmware")

        params = [0.0] * 7
        action = intent.action
        if action == "ARM":
            command = CMD_ARM_DISARM
            params[0] = 1.0
            params[1] = 0.0   # ArduPilot safety checks always enforced.
        elif action == "DISARM":
            command = CMD_ARM_DISARM
            params[0] = 0.0
            params[1] = 0.0   # Never send the force-disarm magic value.
        elif action == "TAKEOFF":
            command = CMD_TAKEOFF
            params[6] = float(intent.params["relative_alt_m"])
        elif action == "LAND":
            command = CMD_LAND
        elif action == "RTL":
            command = CMD_RTL
        elif action == "SET_MODE":
            command = CMD_SET_MODE
            params[0] = float(CUSTOM_MODE_FLAG)
            params[1] = float(COPTER_MODES[intent.params["mode"]])
        elif action == "MISSION_START":
            command = CMD_MISSION_START
        else:
            # PARAM_SET uses a typed PARAM_SET/PARAM_VALUE handshake;
            # MISSION_UPLOAD uses MISSION_COUNT/REQUEST_INT/ITEM_INT/ACK.
            # Sending either as COMMAND_LONG would be false completion.
            raise ControlDenied("Action requires separate validated MAVLink microservice")

        self.port.mav.command_long_send(
            self.system_id, 1, command, 0, *params)
        self.pending = intent.request_id, command
        return {"request_id": intent.request_id, "status": "UART_SENT_ACK_PENDING",
                "command_id": command, "fc_ack_received": False,
                "flight_executed": False}

    def observe_command_ack(self, *, source_system: int,
                            source_component: int, command: int,
                            result: int) -> dict | None:
        if self.pending is None:
            return None
        request_id, expected = self.pending
        if (source_system != self.system_id or source_component != 1 or
            command != expected or type(result) is not int or
            not 0 <= result <= 255):
            return None
        if result != 5:  # IN_PROGRESS remains outstanding.
            self.pending = None
        return {"request_id": request_id, "fc_ack_received": True,
                "result": result, "status":
                    "FC_ACCEPTED_NOT_EXECUTION_VERIFIED" if result == 0
                    else "FC_IN_PROGRESS" if result == 5 else "FC_REJECTED",
                "flight_executed": False}

    def abort(self) -> None:
        """Forget a pending command; never issue an implicit remote disarm."""
        self.pending = None
