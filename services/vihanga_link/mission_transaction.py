"""Standalone, interlocked MAVLink mission upload transaction for future Pi integration.

This module does not open the FC UART or accept network packets. The caller
must prove the local hardware interlock and physical FC evidence before any
MAVLink action, and must obtain FC-owned mission readback to verify the result.
"""
from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import json
import math
import time
from typing import Any, Callable, Sequence

from .control_policy import ControlDenied, ObservedVehicle
from .pi_control_adapter import PiControlSafety


MAV_FRAME_GLOBAL_RELATIVE_ALT_INT = 6
MAV_CMD_NAV_WAYPOINT = 16
MAV_MISSION_TYPE_MISSION = 0
MAV_MISSION_ACCEPTED = 0


@dataclass(frozen=True)
class MissionWaypoint:
    lat_e7: int
    lon_e7: int
    alt_m: float

    def validate(self) -> None:
        if (type(self.lat_e7) is not int or type(self.lon_e7) is not int or
            not -900000000 <= self.lat_e7 <= 900000000 or
            not -1800000000 <= self.lon_e7 <= 1800000000 or
            self.lat_e7 == 0 and self.lon_e7 == 0 or
            type(self.alt_m) not in (int, float) or
            not math.isfinite(self.alt_m) or
            not 5 <= self.alt_m <= 120):
            raise ControlDenied("Invalid bounded mission waypoint")


def mission_revision(items: Sequence[MissionWaypoint]) -> str:
    if not isinstance(items, (list, tuple)) or not 2 <= len(items) <= 120:
        raise ControlDenied("Mission requires 2–120 approved waypoints")
    for point in items:
        if not isinstance(point, MissionWaypoint):
            raise ControlDenied("Unexpected mission item type")
        point.validate()
    body = {"mission_type": 0, "frame": 6, "command": 16,
            "items": [point.__dict__ for point in items]}
    return sha256(json.dumps(body, sort_keys=True,
                             separators=(",", ":"), allow_nan=False
                             ).encode("utf-8")).hexdigest()


class MissionUploadTransaction:
    """One FC mission upload; no automatic retransmission or mission clear.

    An FC MISSION_ACK ACCEPTED is only delivery acknowledgment; completed
    upload is accepted for operation only after an independent FC download
    verifies identical waypoint values and revision.
    """

    def __init__(self, port: Any, *, vehicle_id: str, system_id: int,
                 items: Sequence[MissionWaypoint], approved_sha256: str,
                 interlock: Callable[[], PiControlSafety] | None = None,
                 clock: Callable[[], float] = time.monotonic):
        if not 1 <= system_id <= 254:
            raise ControlDenied("Exact FC system ID required")
        revision = mission_revision(items)
        if approved_sha256 != revision:
            raise ControlDenied("Mission differs from independent approved revision")
        self.port, self.vehicle_id, self.system_id = port, vehicle_id, system_id
        self.items = tuple(items)
        self.revision = revision
        self.clock = clock
        self.interlock = interlock or (lambda: PiControlSafety())
        self.started = -1.0
        self.state = "NOT_STARTED"
        self.requested: set[int] = set()
        self.resends: dict[int, int] = {}

    def _local_gate(self) -> None:
        gate = self.interlock()
        if not isinstance(gate, PiControlSafety):
            raise ControlDenied("Invalid Pi-local interlock")
        gate.require()

    def _ensure_live(self) -> None:
        if self.state != "WAITING_FOR_FC_REQUEST" or self.started < 0 or (
                self.clock() - self.started > 40):
            self.state = "EXPIRED_OR_OUT_OF_ORDER"
            raise ControlDenied("Mission transaction expired or not waiting for FC")

    def start(self, evidence: ObservedVehicle) -> dict:
        if self.state != "NOT_STARTED":
            raise ControlDenied("Mission upload already started")
        self._local_gate()
        evidence.require_current(self.vehicle_id, self.system_id, navigation=True)
        if evidence.armed or not evidence.on_ground:
            raise ControlDenied("Mission upload requires landed, disarmed FC")
        self.port.mav.mission_count_send(
            self.system_id, 1, len(self.items), MAV_MISSION_TYPE_MISSION)
        self.started = self.clock()
        self.state = "WAITING_FOR_FC_REQUEST"
        return {"status": self.state, "revision_sha256": self.revision,
                "fc_ack_received": False, "readback_verified": False}

    def requested_item(self, *, source_system: int, source_component: int,
                       sequence: int, mission_type: int = 0) -> dict:
        self._ensure_live()
        self._local_gate()
        if (source_system != self.system_id or source_component != 1 or
            mission_type != MAV_MISSION_TYPE_MISSION or
            type(sequence) is not int or not 0 <= sequence < len(self.items)):
            raise ControlDenied("Unmatched FC mission item request")
        repeats = self.resends.get(sequence, 0)
        if repeats >= 3:
            self.state = "ABORTED_DUPLICATE_LIMIT"
            raise ControlDenied("Repeated mission requests exceed safety quota")
        point = self.items[sequence]
        self.port.mav.mission_item_int_send(
            self.system_id, 1, sequence,
            MAV_FRAME_GLOBAL_RELATIVE_ALT_INT, MAV_CMD_NAV_WAYPOINT,
            0, 1, 0., 0., 0., 0.,
            point.lat_e7, point.lon_e7, float(point.alt_m),
            MAV_MISSION_TYPE_MISSION)
        self.resends[sequence] = repeats + 1
        self.requested.add(sequence)
        return {"status": self.state, "requested": len(self.requested),
                "total": len(self.items), "fc_ack_received": False}

    def observe_ack(self, *, source_system: int, source_component: int,
                    result: int, mission_type: int = 0) -> dict:
        self._ensure_live()
        self._local_gate()
        if (source_system != self.system_id or source_component != 1 or
            mission_type != MAV_MISSION_TYPE_MISSION or
            type(result) is not int):
            raise ControlDenied("Untrusted FC mission ACK")
        if result != MAV_MISSION_ACCEPTED:
            self.state = "FC_MISSION_REJECTED"
        elif len(self.requested) != len(self.items):
            self.state = "INCOMPLETE_TRANSFER"
            raise ControlDenied("FC ACK arrived without complete mission transfer")
        else:
            self.state = "FC_ACK_ACCEPTED_READBACK_REQUIRED"
        return {"status": self.state, "fc_ack_received": True,
                "fc_result": result, "readback_verified": False,
                "revision_sha256": self.revision}

    def verify_download(self, actual_fc_items: Sequence[MissionWaypoint]) -> dict:
        if self.state != "FC_ACK_ACCEPTED_READBACK_REQUIRED":
            raise ControlDenied("Cannot claim a mission before real FC ACK")
        self._local_gate()
        try:
            actual_digest = mission_revision(actual_fc_items)
        except ControlDenied as ex:
            self.state = "FC_READBACK_REJECTED"
            raise ControlDenied("Invalid physical mission readback") from ex
        if actual_digest != self.revision:
            self.state = "FC_READBACK_MISMATCH"
            raise ControlDenied("FC readback differs from approved mission")
        self.state = "FC_READBACK_VERIFIED"
        return {"status": self.state, "readback_verified": True,
                "revision_sha256": self.revision, "mission_started": False}

    def abort(self) -> None:
        # Never issue MISSION_CLEAR_ALL automatically: the FC may still have
        # its previous valid plan. Require an explicit readback/reconciliation.
        self.state = "ABORTED_RECONCILE_ON_FC"
