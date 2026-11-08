"""Local hardware-key inputs for the Raspberry Pi sole-UART control worker.

No remotely writable API, environment boolean or network packet can assert
these inputs. Default state (absent file/chip/line/runtime) is always closed.

The four active-high, pull-down digital signals must originate from an
independently verified on-aircraft safety circuit, not GPIO output pins.
"""
from __future__ import annotations

from pathlib import Path
import json
import os
import stat
import threading
from typing import Any

from .pi_control_adapter import PiControlSafety

CONFIG_PATH = Path("/etc/vihanga/pilot-interlock.json")
KEYS = ("key_line", "pilot_line", "radio_line", "override_clear_line")


class LocalGPIOInterlock:
    """libgpiod-v2 input lease; read failures unconditionally deny.

    This class does not modify the GPIO output state. An external physical
    safety circuit and its actual wiring must be separately bench-accepted.
    """

    def __init__(self, path: Path = CONFIG_PATH, gpio: Any = None):
        self.path = path
        self.gpio = gpio
        self._request = None
        self._offsets: dict[str, int] = {}
        self._lock = threading.Lock()
        self.configuration_error = "NOT_CONFIGURED"
        self._setup()

    def _setup(self) -> None:
        try:
            s = self.path.stat()
            if not stat.S_ISREG(s.st_mode) or s.st_uid != 0 or s.st_mode & 0o022:
                raise ValueError("Interlock file must be root-owned and not writable by group/others")
            data = json.loads(self.path.read_text(encoding="utf-8"))
            if not isinstance(data, dict) or set(data) != {"chip", *KEYS}:
                raise ValueError("Exact root-controlled GPIO pin schema required")
            chip = data["chip"]
            if (not isinstance(chip, str) or
                not chip.startswith("/dev/gpiochip") or
                not chip[len("/dev/gpiochip"):].isdigit()):
                raise ValueError("Invalid GPIO chip")
            offsets = {key: data[key] for key in KEYS}
            if any(type(v) is not int or not 0 <= v < 1024 for v in offsets.values()):
                raise ValueError("GPIO line offsets must be integers")
            if len(set(offsets.values())) != 4:
                raise ValueError("Four distinct independently wired input lines required")
            if self.gpio is None:
                import gpiod
                self.gpio = gpiod
            from gpiod.line import Bias, Direction
            settings = self.gpio.LineSettings(
                direction=Direction.INPUT, bias=Bias.PULL_DOWN)
            request = self.gpio.request_lines(
                chip, consumer="vihanga-local-safety", config={
                    line: settings for line in offsets.values()})
            self._offsets = offsets
            self._request = request
            self.configuration_error = ""
        except (OSError, ValueError, TypeError, ImportError, AttributeError, RuntimeError) as exc:
            self.configuration_error = type(exc).__name__
            self._request = None
            self._offsets = {}

    def _read(self) -> dict[str, bool]:
        with self._lock:
            if self._request is None:
                return {key: False for key in KEYS}
            try:
                # gpiod line.Value.ACTIVE is an Enum member. No truthy-string
                # coercion or comparison with a GPIO output's user variable.
                from gpiod.line import Value
                result = {name: self._request.get_value(offset) == Value.ACTIVE
                          for name, offset in self._offsets.items()}
                if set(result) != set(KEYS):
                    raise ValueError("Not all independent lines sampled")
                return result
            except (OSError, ValueError, TypeError, RuntimeError, AttributeError):
                return {key: False for key in KEYS}

    def safety(self) -> PiControlSafety:
        values = self._read()
        return PiControlSafety(
            hardware_interlock_active=values["key_line"],
            local_pilot_present=values["pilot_line"],
            independent_rc_available=values["radio_line"],
            adapter_authorized=values["override_clear_line"],
            firmware_mapping_verified=False,
        )

    def override_clear(self) -> bool:
        return self._read()["override_clear_line"]

    def status(self) -> dict:
        # No pin coordinates or any secrets are shared over the cloud.
        return {
            "configured": self._request is not None,
            "hardware_gate_active": all(self._read().values()),
            "reason": self.configuration_error or "HARDWARE_SAMPLED",
        }

    def close(self) -> None:
        with self._lock:
            if self._request is not None:
                try:
                    self._request.release()
                finally:
                    self._request = None
