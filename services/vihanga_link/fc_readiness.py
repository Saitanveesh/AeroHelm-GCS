"""Flight-controller-owned evidence for an isolated Pi MAVLink session.

All inputs originate from the one UART-reading thread, not HTTP fields. A
missing, stale, wrong-component or ambiguous sensor must never be translated
into healthy control authority. This is a *candidate* safety model requiring
real airframe and firmware acceptance before any powered actuation.
"""
from __future__ import annotations

from dataclasses import asdict
import math
import time
from typing import Any, Callable

from .control_policy import ObservedVehicle
from .pi_control_adapter import COPTER_MODES, PiControlSafety


_REVERSE_MODES = {v: k for k, v in COPTER_MODES.items()}
_EKF_REQUIRED = 1 | 2 | 4 | 16 | 32
_EKF_BAD = 128 | 1024 | 32768
_MAX_AGE = {
    "HEARTBEAT": 1.2, "ATTITUDE": 1.2,
    "GPS_RAW_INT": 1.5, "SYS_STATUS": 3.0,
    "EKF_STATUS_REPORT": 3.0, "FENCE_STATUS": 3.0,
    "RC_CHANNELS": 2.0, "EXTENDED_SYS_STATE": 3.0,
    "HIGH_LATENCY2": 3.0,
}
_ALL_MESSAGES = frozenset(_MAX_AGE)


def _valid_position(lat: Any, lon: Any) -> bool:
    return (type(lat) is int and type(lon) is int and
            abs(lat) <= 900000000 and abs(lon) <= 1800000000 and
            not (lat == 0 and lon == 0))


class PhysicalFCObserver:
    """Conservative FC-owned health snapshot; never injects MAVLink.

    The observer intentionally requires an explicit FC-owned failsafe state
    from HIGH_LATENCY2. Most ordinary telemetry streams do not publish it;
    without that evidence `failsafe_clear` stays False. Never infer global
    failsafe clearance solely from a heartbeat, RC RSSI or SYSTEM_STATUS.
    """

    def __init__(self, vehicle_id: str, system_id: int,
                 clock: Callable[[], float] = time.monotonic):
        if not 1 <= system_id <= 254:
            raise ValueError("Exact FC system ID required")
        self.vehicle_id = vehicle_id
        self.system_id = system_id
        self.clock = clock
        self.reset()

    def reset(self) -> None:
        self.times: dict[str, float] = {}
        self.data: dict[str, dict] = {}
        self.home_present = False
        self.last_boot_ms: int | None = None
        self.heartbeat_at = -1.0
        self.flight_controller_verified = False

    def observe(self, msg: Any) -> bool:
        try:
            kind = msg.get_type()
            if msg.get_srcSystem() != self.system_id or msg.get_srcComponent() != 1:
                return False
        except (AttributeError, TypeError, ValueError):
            return False
        if kind not in _ALL_MESSAGES and kind not in ("HOME_POSITION", "GLOBAL_POSITION_INT"):
            return False
        now = self.clock()
        boot = getattr(msg, "time_boot_ms", None)
        if type(boot) is int:
            if self.last_boot_ms is not None and boot + 1500 < self.last_boot_ms:
                self.reset()  # Reboot even if HEARTBEAT gap was short.
            self.last_boot_ms = boot
        if kind == "HEARTBEAT":
            if self.heartbeat_at >= 0 and now - self.heartbeat_at > 4.0:
                self.reset()
            self.heartbeat_at = now
            self.flight_controller_verified = (
                getattr(msg, "autopilot", None) == 3 and
                getattr(msg, "type", None) in (2, 3, 4, 13, 14) and
                getattr(msg, "system_status", None) in (3, 4))
            self.times[kind] = now
            self.data[kind] = {
                "armed": bool(int(getattr(msg, "base_mode", 0)) & 128),
                "custom_mode": int(getattr(msg, "custom_mode", -1)),
            }
            if not self.flight_controller_verified:
                self.reset()
            return self.flight_controller_verified
        if not self.flight_controller_verified:
            return False
        if kind == "HOME_POSITION":
            self.home_present = _valid_position(
                getattr(msg, "latitude", None), getattr(msg, "longitude", None))
            return self.home_present
        if kind == "GLOBAL_POSITION_INT":
            return _valid_position(getattr(msg, "lat", None), getattr(msg, "lon", None))
        try:
            if kind == "GPS_RAW_INT":
                data = {"fix": int(msg.fix_type)}
            elif kind == "SYS_STATUS":
                data = {"battery": int(msg.battery_remaining)}
            elif kind == "EKF_STATUS_REPORT":
                data = {"flags": int(msg.flags)}
            elif kind == "FENCE_STATUS":
                data = {"breach": int(msg.breach_status)}
            elif kind == "RC_CHANNELS":
                channels = [int(getattr(msg, f"chan{i}_raw", 65535)) for i in range(1, 5)]
                data = {"count": int(msg.chancount), "rssi": int(msg.rssi),
                        "valid_channels": all(900 <= p <= 2100 for p in channels)}
            elif kind == "EXTENDED_SYS_STATE":
                data = {"landed": int(msg.landed_state)}
            elif kind == "HIGH_LATENCY2":
                data = {"failsafe": int(msg.failsafe)}
            elif kind == "ATTITUDE":
                data = {"finite": all(math.isfinite(float(getattr(msg, a)))
                                      for a in ("roll", "pitch", "yaw"))}
            else:
                return False
        except (AttributeError, ValueError, TypeError, OverflowError):
            return False
        self.times[kind] = now
        self.data[kind] = data
        return True

    def _fresh(self, name: str) -> bool:
        age = self.clock() - self.times.get(name, -1e20)
        return 0 <= age <= _MAX_AGE[name]

    def _age(self, name: str) -> int:
        stamp = self.times.get(name)
        return max(0, int(1000 * (self.clock() - stamp))) if stamp is not None else 999999

    def evidence(self, local: PiControlSafety | None = None,
                 *, override_clear: bool = False) -> ObservedVehicle:
        local = local if isinstance(local, PiControlSafety) else PiControlSafety()
        hb = self.data.get("HEARTBEAT", {})
        gps = self.data.get("GPS_RAW_INT", {})
        batt = self.data.get("SYS_STATUS", {})
        ekf = self.data.get("EKF_STATUS_REPORT", {})
        rc = self.data.get("RC_CHANNELS", {})
        fence = self.data.get("FENCE_STATUS", {})
        ext = self.data.get("EXTENDED_SYS_STATE", {})
        hl = self.data.get("HIGH_LATENCY2", {})
        flags = ekf.get("flags", 0)
        armed = hb.get("armed", False) if self._fresh("HEARTBEAT") else False
        mode_id = hb.get("custom_mode", -1) if self._fresh("HEARTBEAT") else -1
        battery = batt.get("battery") if self._fresh("SYS_STATUS") else None
        if battery is not None and not 0 <= battery <= 100:
            battery = None
        return ObservedVehicle(
            vehicle_id=self.vehicle_id, system_id=self.system_id,
            source="PHYSICAL_FC",
            heartbeat_age_ms=self._age("HEARTBEAT"),
            attitude_age_ms=self._age("ATTITUDE"),
            gps_age_ms=self._age("GPS_RAW_INT") if self._fresh("GPS_RAW_INT") else 999999,
            gps_fix=gps.get("fix") if self._fresh("GPS_RAW_INT") else None,
            home_verified=self.home_present and self._fresh("HEARTBEAT"),
            battery_percent=battery,
            ekf_healthy=(self._fresh("EKF_STATUS_REPORT") and
                         (flags & _EKF_REQUIRED) == _EKF_REQUIRED and
                         (flags & _EKF_BAD) == 0),
            geofence_healthy=self._fresh("FENCE_STATUS") and fence.get("breach") == 0,
            rc_link_healthy=(self._fresh("RC_CHANNELS") and rc.get("count", 0) >= 4 and
                             0 < rc.get("rssi", 255) < 255 and
                             rc.get("valid_channels", False) and
                             local.independent_rc_available is True),
            rc_override_active=not (override_clear is True),
            on_ground=self._fresh("EXTENDED_SYS_STATE") and ext.get("landed") == 1,
            armed=armed, mode=_REVERSE_MODES.get(mode_id, ""),
            failsafe_clear=(self._fresh("HIGH_LATENCY2") and
                            hl.get("failsafe") == 0 and self.flight_controller_verified),
        )

    def report(self, local: PiControlSafety | None = None,
               *, override_clear: bool = False) -> dict:
        evidence = self.evidence(local, override_clear=override_clear)
        needed = ("HEARTBEAT", "ATTITUDE", "GPS_RAW_INT", "SYS_STATUS",
                  "EKF_STATUS_REPORT", "FENCE_STATUS", "RC_CHANNELS",
                  "EXTENDED_SYS_STATE", "HIGH_LATENCY2")
        stale = [name for name in needed if not self._fresh(name)]
        return {
            "fields": asdict(evidence),
            "fresh_source_ids": sorted(name for name in needed if self._fresh(name)),
            "stale_or_missing": stale,
            "physical_fc_verified": self.flight_controller_verified,
            "home_observed_in_fc_epoch": self.home_present,
            "physical_control_qualified": False,  # Never self-certify hardware.
        }
