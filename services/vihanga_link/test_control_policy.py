"""Offline control-authority tests. No Pi connection or aircraft commands."""
import unittest
from dataclasses import replace
from services.vihanga_link.control_policy import (
    ControlDenied, FlightControlBroker, FlightIntent, ObservedVehicle,
)


class Clock:
    def __init__(self): self.now = 10.0
    def __call__(self): return self.now
    def advance(self, seconds): self.now += seconds


PLAN = "a" * 64
AIRCRAFT = "AH-HW-001"


def valid_evidence(**kwargs):
    data = {
        "vehicle_id": AIRCRAFT, "system_id": 1, "source": "PHYSICAL_FC",
        "heartbeat_age_ms": 200, "attitude_age_ms": 150,
        "gps_age_ms": 350, "gps_fix": 3, "home_verified": True,
        "battery_percent": 81., "ekf_healthy": True,
        "geofence_healthy": True, "rc_link_healthy": True,
        "rc_override_active": False, "on_ground": True,
        "armed": False, "mode": "GUIDED", "failsafe_clear": True,
    }
    data.update(kwargs)
    return ObservedVehicle(**data)


def intent(action="ARM", sequence=1, params=None, rid="b" * 32):
    return FlightIntent(AIRCRAFT, rid, sequence, action, params or {}, PLAN)


class ControlPolicyTests(unittest.TestCase):
    def setUp(self):
        self.clock = Clock()
        self.broker = FlightControlBroker(clock=self.clock)
        self.lease = self.broker.acquire(
            vehicle_id=AIRCRAFT, system_id=1, pilot="pilot1",
            approver="safety1", plan_sha256=PLAN, control_epoch=7,
        )

    def submit(self, cmd=None, evidence=None):
        return self.broker.submit(cmd or intent(), lease_token=self.lease["lease_token"],
                                  evidence=evidence or valid_evidence(), epoch=7)

    def test_two_person_lease_and_exclusive_controller(self):
        with self.assertRaises(ControlDenied):
            self.broker.acquire(vehicle_id=AIRCRAFT, system_id=1,
                                pilot="same", approver="same",
                                plan_sha256=PLAN, control_epoch=7)
        with self.assertRaises(ControlDenied):
            self.broker.acquire(vehicle_id=AIRCRAFT, system_id=1,
                                pilot="pilot2", approver="safety2",
                                plan_sha256=PLAN, control_epoch=7)

    def test_whitelist_rejects_force_arm_arbitrary_mavlink_and_safety_bypass(self):
        for action, params in [
            ("COMMAND_LONG", {"command": 400}),
            ("ARM", {"force": True}),
            ("DISARM", {"force": 21196}),
            ("PARAM_SET", {"name": "ARMING_CHECK", "value": 0}),
            ("TAKEOFF", {"relative_alt_m": 999}),
            ("SET_MODE", {"mode": "UNKNOWN"}),
        ]:
            with self.subTest(action=action, params=params):
                with self.assertRaises(ControlDenied):
                    intent(action, params=params).verify()

    def test_all_transport_acks_are_separate_from_fc_execution(self):
        cmd = intent()
        queued = self.submit(cmd)
        self.assertEqual("AUTHORIZED_NOT_DISPATCHED", queued["status"])
        self.assertFalse(queued["flight_executed"])
        with self.assertRaises(ControlDenied):
            self.broker.fc_result(cmd.request_id, result=0, fc_system_id=1,
                                  fc_component_id=1, command_id=400)
        self.broker.transport_sent(cmd.request_id)
        ack = self.broker.pi_result(cmd.request_id, accepted=True, interlock=True)
        self.assertEqual("PI_RECEIVED", ack["status"])
        with self.assertRaises(ControlDenied):
            self.broker.fc_result(cmd.request_id, result=0, fc_system_id=2,
                                  fc_component_id=1, command_id=400)
        with self.assertRaises(ControlDenied):
            self.broker.fc_result(cmd.request_id, result=0, fc_system_id=1,
                                  fc_component_id=1, command_id=20)
        receipt = self.broker.fc_result(cmd.request_id, result=0, fc_system_id=1,
                                        fc_component_id=1, command_id=400)
        self.assertEqual("FC_ACCEPTED_NOT_EXECUTION_VERIFIED", receipt["status"])
        self.assertEqual(0, receipt["fc_ack_result"])
        self.assertFalse(receipt["flight_executed"])

    def test_no_replay_no_mismatched_id_no_overlapping_commands(self):
        cmd = intent()
        self.submit(cmd)
        self.assertEqual("AUTHORIZED_NOT_DISPATCHED", self.submit(cmd)["status"])
        with self.assertRaises(ControlDenied):
            self.submit(replace(cmd, params={"force": True}))
        with self.assertRaises(ControlDenied):
            self.submit(intent("LAND", 2, rid="c"*32), valid_evidence(armed=True))
        self.broker.pi_result(cmd.request_id, accepted=False, interlock=False)
        receipt = self.submit(intent("LAND", 2, rid="c"*32),
                              valid_evidence(armed=True))
        self.assertEqual("AUTHORIZED_NOT_DISPATCHED", receipt["status"])

    def test_failsafe_and_pilot_override_fail_closed(self):
        for kwargs in [
            {"source": "OPERATOR_ASSERTED"},
            {"heartbeat_age_ms": 4000},
            {"attitude_age_ms": 1500},
            {"rc_link_healthy": False},
            {"rc_override_active": True},
            {"failsafe_clear": False},
            {"gps_fix": 2},
            {"gps_age_ms": None},
            {"home_verified": False},
            {"battery_percent": 12.},
            {"ekf_healthy": False},
            {"geofence_healthy": False},
            {"vehicle_id": "WRONG"},
            {"system_id": 2},
        ]:
            with self.subTest(kwargs=kwargs):
                with self.assertRaises(ControlDenied):
                    self.submit(evidence=valid_evidence(**kwargs))

    def test_land_can_be_considered_without_fresh_gps(self):
        state = valid_evidence(armed=True, on_ground=False,
                               gps_fix=None, gps_age_ms=None,
                               home_verified=False, mode="ALT_HOLD")
        record = self.submit(intent("LAND"), state)
        self.assertEqual("AUTHORIZED_NOT_DISPATCHED", record["status"])

    def test_takeoff_requires_armed_guided_navigation(self):
        with self.assertRaises(ControlDenied):
            self.submit(intent("TAKEOFF", params={"relative_alt_m": 10}))
        state = valid_evidence(armed=True, on_ground=False, mode="GUIDED")
        record = self.submit(intent("TAKEOFF", params={"relative_alt_m": 10}), state)
        self.assertEqual("AUTHORIZED_NOT_DISPATCHED", record["status"])

    def test_preparation_requires_landed_disarmed_and_allowlisted_params(self):
        record = self.submit(intent("PARAM_SET",
                                    params={"name": "WPNAV_SPEED", "value": 350}))
        self.assertEqual("AUTHORIZED_NOT_DISPATCHED", record["status"])
        self.broker.revoke()
        self.lease = self.broker.acquire(vehicle_id=AIRCRAFT, system_id=1,
                                          pilot="pilot1", approver="safety1",
                                          plan_sha256=PLAN, control_epoch=8)
        with self.assertRaises(ControlDenied):
            self.broker.submit(
                intent("MISSION_UPLOAD", params={"revision": PLAN}),
                lease_token=self.lease["lease_token"],
                evidence=valid_evidence(armed=True, on_ground=False), epoch=8)

    def test_expired_lease_or_command_never_replays(self):
        c = intent()
        self.submit(c)
        self.clock.advance(3.1)
        with self.assertRaises(ControlDenied):
            self.broker.pi_result(c.request_id, accepted=True, interlock=True)
        self.assertEqual(1, self.broker.expire())
        self.assertEqual("EXPIRED_NO_REPLAY", self.broker.receipt(c.request_id)["status"])
        self.clock.advance(30)
        with self.assertRaises(ControlDenied):
            self.submit(intent("LAND", 2, rid="d"*32), valid_evidence(armed=True))

    def test_epoch_and_approval_revision_bound(self):
        with self.assertRaises(ControlDenied):
            self.broker.submit(intent(), lease_token=self.lease["lease_token"],
                               evidence=valid_evidence(), epoch=8)
        with self.assertRaises(ControlDenied):
            self.submit(replace(intent(), plan_sha256="c"*64))

    def test_audit_chain_has_linked_hashes(self):
        self.submit(intent())
        events = self.broker.audit
        self.assertEqual("0"*64, events[0]["previous"])
        for prior, next_entry in zip(events, events[1:]):
            self.assertEqual(prior["hash"], next_entry["previous"])


if __name__ == "__main__":
    unittest.main()
