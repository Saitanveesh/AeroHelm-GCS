"""Readback-gated parameter mutation tests; no physical UART."""
from dataclasses import replace
import unittest

from services.vihanga_link.control_policy import ControlDenied
from services.vihanga_link.pi_control_adapter import PiControlSafety
from services.vihanga_link.parameter_transaction import (
    ParameterWriteTransaction, MAV_PARAM_TYPE_REAL32,
)
from services.vihanga_link.test_control_policy import intent, valid_evidence


class Port:
    class Mav:
        def __init__(self): self.commands = []
        def param_set_send(self, *args): self.commands.append(args)
    def __init__(self): self.mav = self.Mav()


class Clock:
    def __init__(self): self.t = 10.
    def __call__(self): return self.t


class ParameterTests(unittest.TestCase):
    def setUp(self):
        self.port = Port()
        self.clock = Clock()
        self.key = PiControlSafety(True, True, True, True, True)
        self.writer = ParameterWriteTransaction(
            self.port, system_id=1, interlock=lambda: self.key,
            clock=self.clock)
        self.cmd = intent("PARAM_SET", params={
            "name": "WPNAV_SPEED", "value": 350,
        })

    def test_no_key_no_write(self):
        default = ParameterWriteTransaction(self.port, system_id=1)
        with self.assertRaises(ControlDenied):
            default.send(self.cmd, valid_evidence(), MAV_PARAM_TYPE_REAL32)
        self.assertEqual([], self.port.mav.commands)

    def test_no_unsafe_parameter_keys_or_extreme_values(self):
        for name, value in (
            ("ARMING_CHECK", 0), ("FS_GCS_ENABLE", 0), ("SERIAL1_PROTOCOL", 1),
            ("WPNAV_SPEED", 50000), ("RTL_ALT", 0),
        ):
            with self.subTest(name=name), self.assertRaises(ControlDenied):
                self.writer.send(intent("PARAM_SET", params={
                    "name": name, "value": value,
                }), valid_evidence(), MAV_PARAM_TYPE_REAL32)
        self.assertEqual([], self.port.mav.commands)

    def test_actual_type_and_ground_disarmed_required(self):
        with self.assertRaises(ControlDenied):
            self.writer.send(self.cmd, valid_evidence(), 4)
        with self.assertRaises(ControlDenied):
            self.writer.send(self.cmd, valid_evidence(armed=True), 9)
        with self.assertRaises(ControlDenied):
            self.writer.send(self.cmd, valid_evidence(on_ground=False), 9)
        self.assertEqual([], self.port.mav.commands)

    def test_set_requires_exact_matching_fc_value_to_finish(self):
        first = self.writer.send(self.cmd, valid_evidence(), 9)
        self.assertEqual("PARAM_SET_SENT_AWAITING_FC_VALUE", first["status"])
        self.assertFalse(first["parameter_write_completed"])
        command = self.port.mav.commands[0]
        self.assertEqual((1, 1), command[:2])
        self.assertEqual(b"WPNAV_SPEED".ljust(16, b"\0"), command[2])
        self.assertEqual(350., command[3])
        self.assertEqual(9, command[4])
        self.assertIsNone(self.writer.observe_value(
            source_system=2, source_component=1,
            name="WPNAV_SPEED", value=350., parameter_type=9))
        self.assertIsNone(self.writer.observe_value(
            source_system=1, source_component=2,
            name="WPNAV_SPEED", value=350., parameter_type=9))
        self.assertIsNone(self.writer.observe_value(
            source_system=1, source_component=1,
            name="WRONG", value=350., parameter_type=9))
        result = self.writer.observe_value(
            source_system=1, source_component=1,
            name="WPNAV_SPEED", value=350., parameter_type=9)
        self.assertTrue(result["verified_readback"])
        self.assertTrue(result["parameter_write_completed"])
        self.assertEqual("FC_PARAM_VALUE_VERIFIED", result["status"])

    def test_mismatch_is_failure_not_confirmation(self):
        self.writer.send(self.cmd, valid_evidence(), 9)
        result = self.writer.observe_value(
            source_system=1, source_component=1,
            name="WPNAV_SPEED", value=999., parameter_type=9)
        self.assertEqual("FC_PARAM_VALUE_MISMATCH", result["status"])
        self.assertFalse(result["parameter_write_completed"])

    def test_timeout_does_not_automatically_retry(self):
        self.writer.send(self.cmd, valid_evidence(), 9)
        self.clock.t += 5
        status = self.writer.observe_value(
            source_system=1, source_component=1,
            name="WPNAV_SPEED", value=350., parameter_type=9)
        self.assertEqual("TIMEOUT_NO_REPLAY", status["status"])
        self.assertEqual(1, len(self.port.mav.commands))
        self.writer.abort()
        self.assertEqual("ABORTED_RECONCILE_READBACK", self.writer.status)


if __name__ == "__main__":
    unittest.main()
