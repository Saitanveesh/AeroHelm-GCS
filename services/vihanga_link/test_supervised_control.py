"""Contract tests: no physical MAVLink, power or aircraft connection."""
import pytest

from services.vihanga_link.supervised_control import (
    AuthorityRejected, SimulatedControlPlane, VehicleEvidence,
)

PLAN = "a" * 64


def evidence(*, disarmed=True, heartbeat_age_ms=100, **updates):
    fields = dict(
        vehicle_id="AH-HW-001", system_id=1,
        heartbeat_age_ms=heartbeat_age_ms, attitude_age_ms=100,
        source="SITL", disarmed=disarmed, gps_fix=3,
        ekf_healthy=True, geofence_healthy=True,
        battery_percent=90.0, pilot_override_available=True,
        failsafe_clear=True,
    )
    fields.update(updates)
    return VehicleEvidence(**fields)


def control():
    tick = [100.0]
    return SimulatedControlPlane(clock=lambda: tick[0]), tick


def test_requires_independent_approval_exclusive_lease_and_expiry():
    c, tick = control()
    with pytest.raises(AuthorityRejected):
        c.acquire("AH-HW-001", 1, "pilot", "pilot", PLAN)
    l = c.acquire("AH-HW-001", 1, "pilot", "safety-officer", PLAN, 30)
    assert l["scope"] == "SIMULATOR_ONLY"
    with pytest.raises(AuthorityRejected, match="exclusive"):
        c.acquire("AH-HW-001", 1, "other", "safety-officer", PLAN)
    tick[0] += 31
    with pytest.raises(AuthorityRejected, match="expired"):
        c._lease("AH-HW-001", l["lease_token"])
    assert c.acquire("AH-HW-001", 1, "pilot", "other", PLAN)


def test_parameter_write_requires_disarm_and_two_distinct_acks():
    c, _ = control()
    l = c.acquire("AH-HW-001", 1, "pilot", "reviewer", PLAN)
    args = dict(vehicle_id="AH-HW-001", lease_token=l["lease_token"],
                request_id="1" * 32, sequence=1, action="PARAM_WRITE_SIM",
                payload={"name": "SR1_EXTRA1", "value": 5.0},
                plan_digest=PLAN, evidence=evidence())
    receipt = c.submit(**args)
    assert receipt["status"] == "SIM_VALIDATED"
    assert not receipt["hardware_actuated"]
    assert not receipt["fc_result_ack"]
    assert c.submit(**args) == receipt
    with pytest.raises(AuthorityRejected):
        c.submit(**{**args, "payload": {"name": "SR1_EXTRA1", "value": 10.0}})
    with pytest.raises(AuthorityRejected):
        c.observe_fc_result(args["request_id"], True, evidence())
    assert c.observe_pi_ack(args["request_id"], True)["status"] == "PI_ACCEPTED_SIM"
    assert c.parameters == {}
    assert c.observe_fc_result(args["request_id"], True, evidence())[
        "status"] == "FC_ACCEPTED_SIM"
    assert c.parameters["AH-HW-001"]["SR1_EXTRA1"] == 5.0
    with pytest.raises(AuthorityRejected):
        c.observe_fc_result(args["request_id"], True, evidence())


def test_mission_upload_then_execute_requires_fc_confirmation_and_armed_sim():
    c, _ = control()
    l = c.acquire("AH-HW-001", 1, "pilot", "reviewer", PLAN)
    base = dict(vehicle_id="AH-HW-001", lease_token=l["lease_token"],
                plan_digest=PLAN)
    with pytest.raises(AuthorityRejected):
        c.submit(**base, request_id="2" * 32, sequence=1,
                 action="MISSION_START_SIM",
                 payload={"revision_sha256": PLAN}, evidence=evidence(disarmed=False))
    uplink = c.submit(**base, request_id="3" * 32, sequence=1,
                      action="MISSION_UPLOAD_SIM",
                      payload={"revision_sha256": PLAN}, evidence=evidence())
    assert uplink["status"] == "SIM_VALIDATED"
    c.observe_pi_ack("3" * 32, True)
    c.observe_fc_result("3" * 32, True, evidence())
    started = c.submit(**base, request_id="4" * 32, sequence=2,
                       action="MISSION_START_SIM",
                       payload={"revision_sha256": PLAN},
                       evidence=evidence(disarmed=False))
    assert started["status"] == "SIM_VALIDATED"
    assert not started["hardware_actuated"]


@pytest.mark.parametrize("bad", [
    {"heartbeat_age_ms": 1600},
    {"attitude_age_ms": 1600},
    {"source": "fabricated"},
    {"battery_percent": 5.0},
    {"ekf_healthy": False},
    {"geofence_healthy": False},
    {"pilot_override_available": False},
    {"failsafe_clear": False},
    {"system_id": 2},
])
def test_untrusted_safety_evidence_rejected(bad):
    c, _ = control()
    l = c.acquire("AH-HW-001", 1, "pilot", "reviewer", PLAN)
    with pytest.raises(AuthorityRejected):
        c.submit(vehicle_id="AH-HW-001", lease_token=l["lease_token"],
                 request_id="5" * 32, sequence=1, action="PARAM_WRITE_SIM",
                 payload={"name": "SR1_EXTRA1", "value": 5},
                 plan_digest=PLAN, evidence=evidence(**bad))


def test_expired_receipt_rejects_late_ack_and_cannot_mutate_parameters():
    c, tick = control()
    l = c.acquire("AH-HW-001", 1, "pilot", "reviewer", PLAN)
    c.submit(vehicle_id="AH-HW-001", lease_token=l["lease_token"],
             request_id="6" * 32, sequence=1, action="PARAM_WRITE_SIM",
             payload={"name": "SR1_EXTRA1", "value": 3.0},
             plan_digest=PLAN, evidence=evidence(), ttl_seconds=2)
    tick[0] += 3
    assert c.observe_pi_ack("6" * 32, True)["status"] == "EXPIRED_SIM"
    assert c.parameters == {}
    with pytest.raises(AuthorityRejected):
        c.observe_fc_result("6" * 32, True, evidence())


def test_no_hardware_path_and_no_arbitrary_mavlink():
    c, _ = control()
    assert c.ACTIONS == frozenset({
        "PARAM_WRITE_SIM", "MISSION_UPLOAD_SIM", "MISSION_START_SIM"})
    l = c.acquire("AH-HW-001", 1, "pilot", "reviewer", PLAN)
    with pytest.raises(AuthorityRejected):
        c.submit(vehicle_id="AH-HW-001", lease_token=l["lease_token"],
                 request_id="7" * 32, sequence=1, action="ARM",
                 payload={"mavlink": "COMMAND_LONG"},
                 plan_digest=PLAN, evidence=evidence())
