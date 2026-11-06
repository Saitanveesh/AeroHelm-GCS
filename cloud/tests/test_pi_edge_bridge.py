"""No-hardware Pi->Oracle observation path security and failure tests."""
from __future__ import annotations

import io
import json
import os
import stat
import time
from urllib import error, request

import pytest

from cloud.edge.bridge import (
    MAX_AGE_MS, ObservationBridge, PrivateCloud, normalize,
)
from cloud.edge.identity import DeviceTokenProvider, validated_https_url


class FakeReply:
    def __init__(self, status: int, data: dict):
        self.status = status
        self.contents = io.BytesIO(json.dumps(data).encode())

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def read(self, count):
        return self.contents.read(count)


class FakeHTTP:
    def __init__(self):
        self.calls = []
        self.auth = 0

    def open(self, req, timeout):
        self.calls.append((req.full_url, req.get_method(), req.data))
        if req.full_url.endswith("/token"):
            self.auth += 1
            return FakeReply(200, {
                "access_token": "x" * 128, "token_type": "Bearer",
                "expires_in": 60,
            })
        if req.full_url.endswith("/telemetry/session"):
            return FakeReply(200, {"session_epoch": 1})
        if req.full_url.endswith("/telemetry"):
            body = json.loads(req.data)
            return FakeReply(202, {
                "accepted": True, "sequence": body["sequence"],
                "session_epoch": body["epoch"],
            })
        raise ValueError("Unexpected API destination")


def make_secret(tmp_path):
    secret = tmp_path / "client-secret"
    secret.write_text("test-secret-abcdefghijklmnopqrstuvwxyz-123")
    secret.chmod(0o600)
    return str(secret)


def test_client_credentials_are_cached_and_never_embedded_in_url(tmp_path):
    fake = FakeHTTP()
    now = [0.0]
    provider = DeviceTokenProvider(
        token_endpoint="https://idp.example.invalid/token",
        client_id="aerohelm-edge-SIM-A",
        client_secret_file=make_secret(tmp_path),
        opener=fake, clock=lambda: now[0],
    )
    assert provider.get() == "x" * 128
    assert provider.get() == "x" * 128
    assert fake.auth == 1
    now[0] = 50
    provider.get()
    assert fake.auth == 2
    assert all("test-secret" not in url for url, _, _ in fake.calls)
    assert all(url.startswith("https://") for url, _, _ in fake.calls)


@pytest.mark.parametrize("origin", [
    "http://cloud.internal", "https://user:password@cloud.invalid",
    "https://cloud.invalid/path", "https://cloud.invalid?token=foo",
    "https://cloud.invalid/#marker", "https://cloud.invalid\\path",
])
def test_untrusted_cloud_origins_are_rejected(origin):
    with pytest.raises(ValueError):
        validated_https_url(origin, permit_path=False)


def test_secret_permissions_checked_before_auth(tmp_path):
    secret = make_secret(tmp_path)
    os.chmod(secret, 0o644)
    provider = DeviceTokenProvider(
        token_endpoint="https://idp.example.invalid/token",
        client_id="aerohelm-edge-SIM-A",
        client_secret_file=secret, opener=FakeHTTP(),
    )
    with pytest.raises(ValueError, match="owner"):
        provider.get()


def test_private_cloud_only_sends_observations(tmp_path):
    fake = FakeHTTP()
    identity = DeviceTokenProvider(
        token_endpoint="https://idp.example.invalid/token",
        client_id="aerohelm-edge-SIM-A",
        client_secret_file=make_secret(tmp_path), opener=fake,
    )
    cloud = PrivateCloud(
        origin="https://cloud.internal", token_provider=identity,
        opener=fake,
    )
    assert cloud.session() == 1
    cloud.sample({"mode": "STANDBY", "armed": False}, 1, 1)
    with pytest.raises(ValueError, match="control"):
        cloud.post("/api/v1/device/jobs/claim", {})
    with pytest.raises(ValueError, match="control"):
        cloud.post("/api/v1/device/telemetry/exec", {})
    assert len(fake.calls) == 3  # IdP, session, observation.


def test_drops_bad_and_stale_samples_without_network_io():
    now = 1770000000000
    assert normalize({
        "captured_at_ms": now - MAX_AGE_MS - 1,
        "mode": "STANDBY", "armed": False,
    }, now_ms=now) is None
    assert normalize({"captured_at_ms": now, "mode": "STANDBY",
                      "armed": False, "command": "ARM"}, now_ms=now) is None
    assert normalize({"captured_at_ms": now, "mode": "STANDBY",
                      "armed": False, "battery_pct": float("nan")}, now_ms=now) is None
    assert normalize({"captured_at_ms": now, "mode": "STANDBY",
                      "armed": False, "gps_fix_type": True}, now_ms=now) is None
    assert normalize({"captured_at_ms": now, "mode": "STANDBY",
                      "armed": False, "latitude": 12.9716}, now_ms=now) == {
                          "mode": "STANDBY", "armed": False,
                          "latitude": 12.9716,
                      }


class StubCloud:
    def __init__(self, *, fail=False):
        self.fail = fail
        self.sessions = 0
        self.sent = []

    def session(self):
        self.sessions += 1
        return self.sessions

    def sample(self, body, epoch, seq):
        if self.fail:
            self.fail = False
            raise OSError("network break")
        self.sent.append((body, epoch, seq))


def test_bounded_backpressure_drops_oldest_and_never_blocks_uart():
    now = [1770000000.0]
    fake = StubCloud()
    bridge = ObservationBridge(fake, clock=lambda: now[0])
    for idx in range(25):
        assert bridge.enqueue({
            "captured_at_ms": int(now[0]*1000),
            "mode": "STANDBY", "armed": False,
            "latitude": 12.0 + idx / 1000,
        })
    assert bridge.samples.qsize() == 16
    assert bridge.dropped == 9
    while not bridge.samples.empty():
        bridge.samples.get_nowait()


def test_failed_observation_is_not_resent_with_old_epoch():
    now = [1770000000.0]
    fake = StubCloud(fail=True)
    bridge = ObservationBridge(fake, clock=lambda: now[0])
    bridge.enqueue({
        "captured_at_ms": int(now[0]*1000), "armed": False,
        "mode": "STANDBY",
    })
    bridge.start()
    # Give the sender a bounded interval to observe one injected failure.
    deadline = time.monotonic() + 1.0
    while bridge.dropped == 0 and time.monotonic() < deadline:
        time.sleep(0.01)
    assert bridge.dropped == 1
    # No automatic re-submission of the ambiguous sample.
    assert fake.sent == []
    bridge.stop()
    assert fake.sessions == 1


def test_no_mavlink_or_second_uart_imports():
    from pathlib import Path
    root = Path(__file__).resolve().parents[1] / "edge"
    source = "\n".join(x.read_text(encoding="utf-8")
                       for x in root.glob("*.py"))
    for forbidden in (
        "pymavlink", "mavutil", "Serial(", "command_long_send(",
        "param_set_send(", "os.system(", "subprocess.run(",
    ):
        assert forbidden not in source
