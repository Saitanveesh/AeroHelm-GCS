"""Strict typed cloud command protocol tests with dummy UART only."""
import json
import time
from types import SimpleNamespace
from dataclasses import asdict

from services.vihanga_link.control_api import ControlContext, ControlSettings
from services.vihanga_link.control_policy import FlightIntent
from services.vihanga_link.test_control_policy import valid_evidence
from services.vihanga_link.typed_command_downlink import (
    PiTypedCommandEngine, outbound_command, parse_pi_command,
)
from services.vihanga_link.pi_control_adapter import PiControlSafety


VEHICLE = "AH-HW-001"
PLAN = "a" * 64


class FakePort:
    class MAV:
        def __init__(self):
            self.sent = []
        def command_long_send(self, *args):
            self.sent.append(args)
    def __init__(self):
        self.mav = self.MAV()


def command(request_id="a" * 32, action="ARM"):
    return FlightIntent(
        VEHICLE, request_id, 1, action, {},
        PLAN, 2500,
    )


def test_typed_transport_rejects_raw_mavlink_other_actions_wrong_identity_and_stale_deadline():
    envelope = outbound_command(command(), VEHICLE, 7)
    assert parse_pi_command(envelope, VEHICLE) == (command(), 7)
    for altered in (
        {**envelope, "mavlink": "fe0100000000"},
        {**envelope, "vehicle_id": "OTHER"},
        {**envelope, "control_epoch": True},
        {**envelope, "control_epoch": -1},
        {**envelope, "expires_at_unix_ms": 0},
        {**envelope, "intent": {**envelope["intent"], "action": "TAKEOFF"}},
        {**envelope, "intent": {**envelope["intent"], "params": {"force": 1}}},
    ):
        assert parse_pi_command(altered, VEHICLE) is None


def test_pi_never_accepts_command_without_authenticated_server_epoch_or_local_key():
    reports = []
    port = FakePort()
    engine = PiTypedCommandEngine(VEHICLE, 1, deliver=reports.append)
    cmd = outbound_command(command(), VEHICLE, 2)
    engine.start_session()
    assert engine.accept(cmd)["status"] == "PI_REFUSED_REPLAY_OR_EPOCH"
    engine.bind_session_epoch(2)
    assert engine.accept(cmd)["type"] == "typed_control_command_queued"
    engine.service_uart(port)
    assert reports[-1]["status"] == "PI_REFUSED_LOCAL_SAFETY"
    assert not reports[-1]["interlock"]
    assert port.mav.sent == []
    assert engine.accept(cmd)["status"] == "PI_REFUSED_REPLAY_OR_EPOCH"
    engine.end_session()


def test_uart_owner_only_emits_one_guarded_physical_command_with_nonforce_param():
    receipts = []
    port = FakePort()
    enabled = PiControlSafety(True, True, True, True, False)
    engine = PiTypedCommandEngine(
        VEHICLE, 1, hardware_gate=lambda: enabled,
        fc_evidence=lambda: valid_evidence(), deliver=receipts.append,
    )
    engine.start_session()
    engine.bind_session_epoch(5)
    cmd = outbound_command(command("b"*32), VEHICLE, 5)
    assert engine.accept(cmd)["type"] == "typed_control_command_queued"
    assert port.mav.sent == []
    engine.service_uart(port)
    assert len(port.mav.sent) == 1
    assert port.mav.sent[0][2] == 400
    assert port.mav.sent[0][4] == 1.0
    assert port.mav.sent[0][5] == 0.0
    assert receipts[-1]["status"] == "PI_UART_SENT_ACK_PENDING"
    assert not receipts[-1]["fc_ack_received"]
    ack = SimpleNamespace(
        get_type=lambda: "COMMAND_ACK", get_srcSystem=lambda: 1,
        get_srcComponent=lambda: 1, command=400, result=0,
    )
    engine.observe_uart_ack(ack)
    assert receipts[-1]["type"] == "typed_control_fc_receipt"
    assert receipts[-1]["fc_ack_received"] is True
    assert receipts[-1]["result"] == 0
    assert len(port.mav.sent) == 1  # No replay of accepted command.
    engine.end_session()


def test_expired_command_refused_on_uart_even_if_queued_before_deadline():
    reports = []
    port = FakePort()
    engine = PiTypedCommandEngine(
        VEHICLE, 1, hardware_gate=lambda: PiControlSafety(True, True, True, True),
        fc_evidence=valid_evidence, deliver=reports.append,
    )
    engine.start_session()
    engine.bind_session_epoch(10)
    msg = outbound_command(command(), VEHICLE, 10)
    assert engine.accept(msg)
    # Inject a stale queued deadline after the authenticated envelope has
    # been checked: verifies the mandatory last-moment deadline recheck.
    with engine._lock:
        queued = engine.commands.get_nowait()
        engine.commands.put_nowait((queued[0], queued[1], 0))
    engine.service_uart(port)
    assert reports[-1]["status"] == "PI_REFUSED_LOCAL_SAFETY"
    assert port.mav.sent == []


def test_authenticated_pi_safety_report_must_be_typed_current_monotonic_and_epoch_bound():
    now = time.monotonic()
    state = SimpleNamespace(
        fc_system_id=1, control_epoch=4, connected=True,
        fc_safety_report=None, fc_safety_report_sequence=0,
        fc_safety_report_at=-1.0,
        last_heartbeat=now, last_attitude=now,
        last_gps_raw_at=now, last_valid_home_at=now,
        fresh=lambda stamp: 0 <= time.monotonic()-stamp < 3,
    )
    settings = ControlSettings(enabled=True, typed_downlink=True)
    control = ControlContext(settings, VEHICLE, state)
    evidence = asdict(valid_evidence())
    msg = {
        "type": "fc_safety_evidence", "vehicle_id": VEHICLE,
        "control_epoch": 4, "sequence": 1,
        "reported_at_unix_ms": int(time.time() * 1000),
        "observation": evidence, "missing_fc_messages": [],
        "physical_control_qualified": False,
    }
    assert control.observe_fc_safety_evidence(json.dumps(msg))
    current = control._physical_evidence()
    assert current.rc_link_healthy and current.ekf_healthy
    assert current.heartbeat_age_ms >= evidence["heartbeat_age_ms"]
    assert not control.observe_fc_safety_evidence(json.dumps(msg))  # replay
    assert not control.observe_fc_safety_evidence(json.dumps(
        {**msg, "sequence": 2, "control_epoch": 3}))
    assert not control.observe_fc_safety_evidence(json.dumps(
        {**msg, "sequence": 2, "observation": {**evidence, "armed": "false"}}))
    assert not control.observe_fc_safety_evidence(json.dumps(
        {**msg, "sequence": 2, "reported_at_unix_ms": 0}))
    state.control_epoch = 5
    assert not control._physical_evidence().rc_link_healthy
