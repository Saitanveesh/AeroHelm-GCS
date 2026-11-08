"""Offline tests for FC-owned command preflight evidence and local keys."""
from __future__ import annotations

from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace

import pytest

from services.vihanga_link.fc_readiness import PhysicalFCObserver
from services.vihanga_link.local_gpio_interlock import LocalGPIOInterlock
from services.vihanga_link.pi_control_adapter import PiControlSafety
from services.vihanga_link.control_policy import ControlDenied


class Clock:
    def __init__(self): self.value = 100.0
    def __call__(self): return self.value
    def advance(self, seconds): self.value += seconds


class FakeFC:
    def __init__(self, name, *, src=1, comp=1, **fields):
        self.name = name
        self.src = src
        self.comp = comp
        self.__dict__.update(fields)
    def get_type(self): return self.name
    def get_srcSystem(self): return self.src
    def get_srcComponent(self): return self.comp


def good_heartbeat(boot=10000):
    return FakeFC("HEARTBEAT", autopilot=3, type=2, system_status=3,
                  base_mode=0, custom_mode=4)


def feed_sensors(observer):
    assert observer.observe(good_heartbeat())
    messages = [
        FakeFC("ATTITUDE", roll=0.1, pitch=0.0, yaw=-0.1, time_boot_ms=10000),
        FakeFC("GPS_RAW_INT", fix_type=3),
        FakeFC("HOME_POSITION", latitude=125000000, longitude=775000000),
        FakeFC("SYS_STATUS", battery_remaining=82),
        FakeFC("EKF_STATUS_REPORT", flags=1|2|4|16|32),
        FakeFC("FENCE_STATUS", breach_status=0),
        FakeFC("RC_CHANNELS", chancount=8, rssi=150, chan1_raw=1500,
               chan2_raw=1500, chan3_raw=1100, chan4_raw=1500),
        FakeFC("EXTENDED_SYS_STATE", landed_state=1),
        FakeFC("HIGH_LATENCY2", failsafe=0),
    ]
    assert all(observer.observe(m) for m in messages)


def test_all_safety_evidence_missing_blocks_navigation_and_report_is_not_release():
    clock = Clock()
    obs = PhysicalFCObserver("AH-HW-001", 1, clock=clock)
    r = obs.evidence()
    assert not r.rc_link_healthy
    assert not r.failsafe_clear
    assert r.rc_override_active
    assert not r.ekf_healthy
    assert not r.on_ground
    assert not r.home_verified
    assert obs.report()["physical_control_qualified"] is False
    with pytest.raises(ControlDenied):
        r.require_current("AH-HW-001", 1, navigation=True)


def test_real_fc_all_sources_plus_local_gate_are_required_but_not_certification():
    clock = Clock()
    obs = PhysicalFCObserver("AH-HW-001", 1, clock=clock)
    feed_sensors(obs)
    assert not obs.evidence().rc_link_healthy  # No independent key by default.
    gate = PiControlSafety(True, True, True, True, False)
    evidence = obs.evidence(gate, override_clear=True)
    assert evidence.home_verified and evidence.ekf_healthy
    assert evidence.geofence_healthy and evidence.rc_link_healthy
    assert evidence.failsafe_clear and evidence.on_ground
    assert evidence.gps_fix == 3 and evidence.battery_percent == 82
    evidence.require_current("AH-HW-001", 1, navigation=True)
    report = obs.report(gate, override_clear=True)
    assert report["stale_or_missing"] == []
    assert report["physical_control_qualified"] is False


@pytest.mark.parametrize("changed,attr", [
    ("GPS_RAW_INT", "gps_fix"),
    ("SYS_STATUS", "battery_percent"),
    ("EKF_STATUS_REPORT", "ekf_healthy"),
    ("FENCE_STATUS", "geofence_healthy"),
    ("RC_CHANNELS", "rc_link_healthy"),
    ("EXTENDED_SYS_STATE", "on_ground"),
    ("HIGH_LATENCY2", "failsafe_clear"),
])
def test_evidence_becomes_unavailable_as_independent_fc_streams_age(changed, attr):
    clock = Clock()
    obs = PhysicalFCObserver("AH-HW-001", 1, clock=clock)
    feed_sensors(obs)
    assert changed in obs.times
    obs.times[changed] = clock() - 20
    gate = PiControlSafety(True, True, True, True, False)
    snap = obs.evidence(gate, override_clear=True)
    assert getattr(snap, attr) in (False, None)


def test_fc_reboot_invalidates_home_and_ekf_and_wrong_identity_never_updates():
    clock = Clock()
    obs = PhysicalFCObserver("AH-HW-001", 1, clock=clock)
    feed_sensors(obs)
    before = dict(obs.times)
    assert not obs.observe(FakeFC("EKF_STATUS_REPORT", src=2, flags=0))
    assert not obs.observe(FakeFC("EKF_STATUS_REPORT", comp=2, flags=0))
    assert before == obs.times
    assert obs.observe(FakeFC("ATTITUDE", time_boot_ms=3000,
                              roll=0, pitch=0, yaw=0)) is False
    assert not obs.home_present and not obs.flight_controller_verified
    assert not obs.evidence().home_verified
    assert obs.observe(good_heartbeat())
    assert not obs.home_present
    assert not obs.evidence().ekf_healthy


def test_uninitialized_ekf_fence_breach_and_unknown_rc_are_all_denied():
    clock = Clock()
    obs = PhysicalFCObserver("AH-HW-001", 1, clock=clock)
    feed_sensors(obs)
    obs.observe(FakeFC("EKF_STATUS_REPORT", flags=1|2|4|16|32|1024))
    obs.observe(FakeFC("FENCE_STATUS", breach_status=1))
    obs.observe(FakeFC("RC_CHANNELS", chancount=8, rssi=255, chan1_raw=1500,
        chan2_raw=1500, chan3_raw=1500, chan4_raw=1500))
    gate = PiControlSafety(True, True, True, True, False)
    s = obs.evidence(gate, override_clear=True)
    assert not s.ekf_healthy and not s.geofence_healthy and not s.rc_link_healthy
    with pytest.raises(ControlDenied):
        s.require_current("AH-HW-001", 1, navigation=True)


def test_missing_root_controlled_gpio_file_cannot_enable_hardware_gate():
    with TemporaryDirectory() as tmp:
        gpio = LocalGPIOInterlock(Path(tmp) / "not-configured.json")
        try:
            gate = gpio.safety()
            assert not gate.hardware_interlock_active
            assert not gate.local_pilot_present
            assert not gate.independent_rc_available
            assert not gate.adapter_authorized
            assert gpio.override_clear() is False
            with pytest.raises(ControlDenied):
                gate.require()
        finally:
            gpio.close()


def test_boolean_coercion_and_fake_readiness_evidence_are_rejected():
    gate = PiControlSafety(hardware_interlock_active="yes",
                           local_pilot_present=True,
                           independent_rc_available=True,
                           adapter_authorized=True)
    with pytest.raises(ControlDenied):
        gate.require()
    clock = Clock()
    obs = PhysicalFCObserver("AH-HW-001", 1, clock=clock)
    feed_sensors(obs)
    from dataclasses import replace
    wrong = replace(obs.evidence(PiControlSafety(True,True,True,True)),
                    failsafe_clear="true")
    with pytest.raises(ControlDenied):
        wrong.require_current("AH-HW-001", 1, navigation=True)
