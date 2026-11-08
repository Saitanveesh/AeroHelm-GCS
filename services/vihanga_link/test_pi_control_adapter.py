"""Physical command encoder tests: mocked UART only, no actual FC."""
import unittest
from dataclasses import replace

from services.vihanga_link.control_policy import ControlDenied
from services.vihanga_link.pi_control_adapter import (
    PiCommandAdapter, PiControlSafety,
    CMD_ARM_DISARM, CMD_TAKEOFF, CMD_LAND, CMD_RTL, CMD_SET_MODE,
)
from services.vihanga_link.test_control_policy import valid_evidence, intent


class DummyPort:
    class MAV:
        def __init__(self):
            self.commands = []

        def command_long_send(self, *args):
            self.commands.append(args)

    def __init__(self):
        self.mav = self.MAV()


class AdapterTests(unittest.TestCase):
    def setUp(self):
        self.port = DummyPort()
        self.interlock = PiControlSafety(True, True, True, True, True)
        self.adapter = PiCommandAdapter(
            self.port, system_id=1, interlock=lambda: self.interlock)

    def test_default_interlock_writes_nothing(self):
        block = PiCommandAdapter(self.port, 1)
        with self.assertRaises(ControlDenied):
            block.send(intent(), valid_evidence())
        self.assertEqual([], self.port.mav.commands)

    def test_any_missing_local_safety_gate_writes_nothing(self):
        for field in ("hardware_interlock_active", "local_pilot_present",
                      "independent_rc_available", "adapter_authorized"):
            gate = replace(self.interlock, **{field: False})
            adapter = PiCommandAdapter(self.port, 1, interlock=lambda g=gate: g)
            with self.subTest(field=field), self.assertRaises(ControlDenied):
                adapter.send(intent(), valid_evidence())
        self.assertEqual([], self.port.mav.commands)

    def test_arm_requires_current_state_and_never_uses_force_magic(self):
        with self.assertRaises(ControlDenied):
            self.adapter.send(intent(), valid_evidence(rc_link_healthy=False))
        with self.assertRaises(ControlDenied):
            self.adapter.send(intent(), valid_evidence(armed=True))
        out = self.adapter.send(intent(), valid_evidence())
        self.assertEqual("UART_SENT_ACK_PENDING", out["status"])
        self.assertEqual(1, len(self.port.mav.commands))
        args = self.port.mav.commands[0]
        self.assertEqual((1, 1, CMD_ARM_DISARM, 0), args[:4])
        self.assertEqual(1., args[4])
        self.assertEqual(0., args[5])
        self.assertNotIn(21196, args)
        self.assertFalse(out["flight_executed"])

    def test_ack_source_and_command_must_match(self):
        self.adapter.send(intent(), valid_evidence())
        self.assertIsNone(self.adapter.observe_command_ack(
            source_system=2, source_component=1, command=400, result=0))
        self.assertIsNone(self.adapter.observe_command_ack(
            source_system=1, source_component=1, command=20, result=0))
        self.assertIsNone(self.adapter.observe_command_ack(
            source_system=1, source_component=2, command=400, result=0))
        receipt = self.adapter.observe_command_ack(
            source_system=1, source_component=1, command=400, result=0)
        self.assertEqual("FC_ACCEPTED_NOT_EXECUTION_VERIFIED", receipt["status"])
        self.assertFalse(receipt["flight_executed"])
        self.assertIsNone(self.adapter.pending)

    def test_busy_command_and_in_progress_ack(self):
        self.adapter.send(intent(), valid_evidence())
        with self.assertRaises(ControlDenied):
            self.adapter.send(intent("LAND", 2, rid="c"*32),
                              valid_evidence(armed=True))
        progress = self.adapter.observe_command_ack(
            source_system=1, source_component=1, command=400, result=5)
        self.assertEqual("FC_IN_PROGRESS", progress["status"])
        self.assertIsNotNone(self.adapter.pending)
        self.adapter.abort()
        self.assertIsNone(self.adapter.pending)
        self.assertEqual(1, len(self.port.mav.commands))

    def test_takeoff_has_bounded_altitude(self):
        takeoff = intent("TAKEOFF", params={"relative_alt_m": 5})
        receipt = self.adapter.send(takeoff,
                                    valid_evidence(armed=True, on_ground=False, mode="GUIDED"))
        self.assertEqual(CMD_TAKEOFF, receipt["command_id"])
        self.assertEqual(5., self.port.mav.commands[0][-1])
        with self.assertRaises(ControlDenied):
            takeoff.__class__(takeoff.aircraft, takeoff.request_id, 1,
                              takeoff.action, {"relative_alt_m": 999},
                              takeoff.plan_sha256).verify()

    def test_disarm_cannot_be_forced_in_air(self):
        ground = valid_evidence(armed=True, on_ground=True, mode="STABILIZE")
        airborne = replace(ground, on_ground=False)
        with self.assertRaises(ControlDenied):
            self.adapter.send(intent("DISARM"), airborne)
        self.assertEqual([], self.port.mav.commands)
        self.adapter.send(intent("DISARM"), ground)
        self.assertEqual(0., self.port.mav.commands[0][4])
        self.assertEqual(0., self.port.mav.commands[0][5])

    def test_param_and_mission_write_require_separate_microservices(self):
        for action, params in [
            ("PARAM_SET", {"name": "WPNAV_SPEED", "value": 350}),
            ("MISSION_UPLOAD", {"revision": "a"*64}),
        ]:
            with self.subTest(action=action), self.assertRaises(ControlDenied):
                self.adapter.send(intent(action, params=params),
                                  valid_evidence())
        self.assertEqual([], self.port.mav.commands)

    def test_mode_mapping_requires_firmware_verification(self):
        guard = replace(self.interlock, firmware_mapping_verified=False)
        adapter = PiCommandAdapter(self.port, 1, interlock=lambda: guard)
        with self.assertRaises(ControlDenied):
            adapter.send(intent("SET_MODE", params={"mode": "RTL"}),
                         valid_evidence(armed=True))
        self.assertEqual([], self.port.mav.commands)
        cmd = self.adapter.send(intent("SET_MODE", params={"mode": "LAND"}),
                                valid_evidence(armed=True))
        self.assertEqual(CMD_SET_MODE, cmd["command_id"])


if __name__ == "__main__":
    unittest.main()
