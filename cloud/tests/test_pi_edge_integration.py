"""End-to-end in-process Pi observation -> real cloud API -> fleet status.

No live network, Pi UART, physical flight controller or Oracle deployment.
"""
from __future__ import annotations

import time

from fastapi.testclient import TestClient

from cloud.app.main import create_app
from cloud.edge.bridge import ObservationBridge
from cloud.tests.test_control_plane import config, jwt_header, register


class APIObservationAdapter:
    def __init__(self, client, headers):
        self.client = client
        self.headers = headers

    def session(self):
        result = self.client.post("/api/v1/device/telemetry/session",
                                  headers=self.headers)
        result.raise_for_status()
        return result.json()["session_epoch"]

    def sample(self, data, epoch, sequence):
        response = self.client.post("/api/v1/device/telemetry",
                                    headers=self.headers,
                                    json={**data,"epoch":epoch,
                                          "sequence":sequence})
        response.raise_for_status()
        assert response.json()["accepted"] is True


def test_pi_like_bridge_to_observation_only_cloud_fleet():
    with TestClient(create_app(config())) as client:
        assert register(client, "PIX-01", kind="hardware").status_code == 201
        header = jwt_header("edge-PIX-01", kind="device", device="PIX-01",
                            scopes="telemetry:write")
        bridge = ObservationBridge(APIObservationAdapter(client, header))
        bridge.start()
        try:
            assert bridge.enqueue({
                "captured_at_ms": int(time.time() * 1000),
                "mode": "STANDBY", "armed": False,
                "latitude": 12.9716, "longitude": 77.5946,
                "battery_pct": 82, "gps_fix_type": 3,
            })
            deadline = time.monotonic() + 3.0
            while bridge.accepted == 0 and time.monotonic() < deadline:
                time.sleep(0.01)
            assert bridge.accepted == 1
            current = client.get("/api/v1/fleet/status",
                                 headers=jwt_header()).json()
            assert current["cloud_flight_authorized"] is False
            vehicle = current["vehicles"][0]
            assert vehicle["kind"] == "hardware"
            assert vehicle["telemetry"]["fresh"] is True
            assert vehicle["telemetry"]["observation"]["battery_pct"] == 82
            assert vehicle["physical_control_enabled"] is False
            assert client.post("/api/v1/vehicles/PIX-01/lease",
                               headers=jwt_header(),
                               json={"duration_seconds": 30}).status_code == 403
        finally:
            bridge.stop()
