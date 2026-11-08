"""MAVLink mission transaction tests; fake serial MAV interface only."""
from dataclasses import replace
import unittest

from services.vihanga_link.control_policy import ControlDenied
from services.vihanga_link.pi_control_adapter import PiControlSafety
from services.vihanga_link.mission_transaction import (
    MissionWaypoint, MissionUploadTransaction, mission_revision,
    MAV_CMD_NAV_WAYPOINT, MAV_FRAME_GLOBAL_RELATIVE_ALT_INT,
)
from services.vihanga_link.test_control_policy import valid_evidence


class Clock:
    def __init__(self): self.now = 100.
    def __call__(self): return self.now


class Port:
    class Mav:
        def __init__(self): self.sent = []
        def mission_count_send(self, *args): self.sent.append(("count", args))
        def mission_item_int_send(self, *args): self.sent.append(("item", args))
    def __init__(self): self.mav = self.Mav()


class MissionTests(unittest.TestCase):
    def setUp(self):
        self.port = Port()
        self.clock = Clock()
        self.items = [
            MissionWaypoint(125000000, 775000000, 30.),
            MissionWaypoint(125001000, 775003000, 35.),
        ]
        self.revision = mission_revision(self.items)
        self.gate = PiControlSafety(True, True, True, True, True)
        self.tx = MissionUploadTransaction(
            self.port, vehicle_id="AH-HW-001", system_id=1, items=self.items,
            approved_sha256=self.revision, interlock=lambda: self.gate,
            clock=self.clock)

    def test_revision_is_exact_and_mission_bounds_enforced(self):
        with self.assertRaises(ControlDenied):
            MissionUploadTransaction(self.port, vehicle_id="AH-HW-001",
                                     system_id=1, items=self.items,
                                     approved_sha256="a"*64,
                                     interlock=lambda: self.gate)
        for alt in (1., 500., float("nan")):
            with self.subTest(alt=alt), self.assertRaises(ControlDenied):
                mission_revision([self.items[0],
                                  MissionWaypoint(125000000, 775000000, alt)])

    def test_no_hardware_key_means_zero_mavlink_frames(self):
        tx = MissionUploadTransaction(
            self.port, vehicle_id="AH-HW-001", system_id=1,
            items=self.items, approved_sha256=self.revision)
        with self.assertRaises(ControlDenied):
            tx.start(valid_evidence())
        self.assertEqual([], self.port.mav.sent)

    def test_upload_requires_ground_disarmed_real_telemetry(self):
        for kwargs in [
            {"armed": True},
            {"on_ground": False},
            {"rc_link_healthy": False},
            {"gps_fix": 2},
            {"geofence_healthy": False},
        ]:
            with self.subTest(kwargs=kwargs), self.assertRaises(ControlDenied):
                self.tx.start(valid_evidence(**kwargs))
        self.assertEqual([], self.port.mav.sent)

    def test_complete_ack_is_not_verified_mission(self):
        initial = self.tx.start(valid_evidence())
        self.assertEqual("WAITING_FOR_FC_REQUEST", initial["status"])
        self.assertEqual(("count", (1, 1, 2, 0)), self.port.mav.sent[0])
        with self.assertRaises(ControlDenied):
            self.tx.requested_item(source_system=2, source_component=1,
                                   sequence=0)
        self.assertEqual(1, len(self.port.mav.sent))
        request = self.tx.requested_item(source_system=1, source_component=1,
                                         sequence=0)
        self.assertEqual(1, request["requested"])
        self.assertEqual("item", self.port.mav.sent[1][0])
        row = self.port.mav.sent[1][1]
        self.assertEqual(MAV_FRAME_GLOBAL_RELATIVE_ALT_INT, row[3])
        self.assertEqual(MAV_CMD_NAV_WAYPOINT, row[4])
        self.assertEqual(self.items[0].lat_e7, row[11])
        with self.assertRaises(ControlDenied):
            self.tx.observe_ack(source_system=1, source_component=1, result=0)
        self.assertEqual("INCOMPLETE_TRANSFER", self.tx.state)

    def test_success_requires_fc_mission_readback(self):
        self.tx.start(valid_evidence())
        for seq in (0, 1):
            self.tx.requested_item(source_system=1, source_component=1, sequence=seq)
        ack = self.tx.observe_ack(source_system=1, source_component=1, result=0)
        self.assertEqual("FC_ACK_ACCEPTED_READBACK_REQUIRED", ack["status"])
        self.assertFalse(ack["readback_verified"])
        with self.assertRaises(ControlDenied):
            self.tx.verify_download([self.items[0],
                                     replace(self.items[1], alt_m=40.)])
        self.assertEqual("FC_READBACK_MISMATCH", self.tx.state)
        # A new complete transaction is required after any mismatch.
        fresh = MissionUploadTransaction(
            Port(), vehicle_id="AH-HW-001", system_id=1, items=self.items,
            approved_sha256=self.revision, interlock=lambda: self.gate)
        fresh.start(valid_evidence())
        for seq in (0, 1):
            fresh.requested_item(source_system=1, source_component=1,
                                 sequence=seq)
        fresh.observe_ack(source_system=1, source_component=1, result=0)
        done = fresh.verify_download(self.items)
        self.assertEqual("FC_READBACK_VERIFIED", done["status"])
        self.assertTrue(done["readback_verified"])
        self.assertFalse(done["mission_started"])

    def test_duplicate_request_quota_timeout_and_abort_do_not_clear_fc(self):
        self.tx.start(valid_evidence())
        for _ in range(3):
            self.tx.requested_item(source_system=1, source_component=1,
                                   sequence=0)
        with self.assertRaises(ControlDenied):
            self.tx.requested_item(source_system=1, source_component=1,
                                   sequence=0)
        self.assertEqual("ABORTED_DUPLICATE_LIMIT", self.tx.state)
        self.assertFalse(any(name == "clear" for name, _ in self.port.mav.sent))
        self.tx.abort()
        self.assertEqual("ABORTED_RECONCILE_ON_FC", self.tx.state)

        again = MissionUploadTransaction(
            self.port, vehicle_id="AH-HW-001", system_id=1,
            items=self.items, approved_sha256=self.revision,
            interlock=lambda: self.gate, clock=self.clock)
        again.start(valid_evidence())
        self.clock.now += 41
        with self.assertRaises(ControlDenied):
            again.requested_item(source_system=1, source_component=1, sequence=0)
        self.assertEqual("EXPIRED_OR_OUT_OF_ORDER", again.state)


if __name__ == "__main__":
    unittest.main()
