"""Deterministic cloud-directed *virtual route* exercise.

No MAVLink, Pixhawk, motors, UART, or flight-capable network I/O.
A virtual worker consumes a scheduled job and publishes a sequence of
normalized, explicitly simulated observations before a workflow-only ACK.
This is NOT SITL, HIL, autopilot guidance, or flight safety evidence.
"""
from __future__ import annotations

import math
import uuid

from cloud.app.planner import TaskSpec, VehicleProfile


class VirtualRouteWorker:
    def __init__(self, client, *, device_id: str, bearer: str,
                 home: VehicleProfile, outbound_steps: int = 6):
        if not device_id.startswith(("SIM-", "AH-SIM-")):
            raise ValueError("Virtual route worker cannot use a physical UAV")
        if not bearer or not 2 <= outbound_steps <= 100:
            raise ValueError("Virtual identity or simulation step budget invalid")
        self.client = client
        self.headers = {"Authorization": "Bearer " + bearer}
        self.device_id = device_id
        self.home = home
        self.steps = outbound_steps

    def _post(self, url: str, body: dict) -> dict:
        response = self.client.post(url, headers=self.headers, json=body)
        response.raise_for_status()
        return response.json()

    def execute_one(self) -> dict:
        receipt = self._post("/api/v1/device/jobs/claim", {})
        job = receipt["job"]
        if job is None:
            return {"status": "IDLE_SIM", "physical_flight_performed": False}
        if (job.get("execution_target") != "VIRTUAL_SIMULATOR_ONLY" or
                job.get("physical_command_authorized") is not False or
                type(job.get("lease_epoch")) is not int or job["lease_epoch"] < 1 or
                not isinstance(job.get("claim_token"), str)):
            raise ValueError("Refusing unsafe virtual job envelope")
        task = TaskSpec.model_validate(job["payload"])
        if task.key != job["task_key"]:
            raise ValueError("Virtual task identity mismatch")

        def ack(phase):
            return self._post("/api/v1/device/jobs/" + job["id"] + "/ack", {
                "event_id": uuid.uuid4().hex, "claim_token": job["claim_token"],
                "phase": phase,
            })

        # A new observation epoch fences old frames from a disconnected edge.
        session = self._post("/api/v1/device/telemetry/session", {})
        epoch = session["session_epoch"]
        if type(epoch) is not int or epoch <= 0:
            raise ValueError("Invalid virtual telemetry session epoch")
        ack("ACCEPTED_SIM")
        sequence = 0
        # Local virtual positions: outbound then return, with no vehicle I/O.
        route = []
        for index in range(1, self.steps+1):
            route.append(index / self.steps)
        for index in range(self.steps-1, -1, -1):
            route.append(index / self.steps)
        for position, fraction in enumerate(route, start=1):
            sequence += 1
            # Deliberately not an autopilot mission simulator. It merely
            # interpolates virtual geometry and decreases fake battery.
            latitude = self.home.home_lat + (
                task.latitude - self.home.home_lat) * fraction
            longitude = self.home.home_lon + (
                task.longitude - self.home.home_lon) * fraction
            altitude = task.relative_altitude_m * fraction
            battery = max(0.0, 95.0 - 15.0 * position / len(route))
            self._post("/api/v1/device/telemetry", {
                "epoch": epoch, "sequence": sequence,
                "latitude": latitude, "longitude": longitude,
                "altitude_m": altitude, "battery_pct": battery,
                "armed": False, "mode": "VIRTUAL_ROUTE",
                "gps_fix_type": 3, "link_rtt_ms": 0,
            })
        # If a telemetry POST fails, never issue a fabricated completion ACK.
        final = ack("COMPLETED_SIM")
        return {
            "status": final["status"], "job_id": job["id"],
            "telemetry_samples": sequence, "physical_flight_performed": False,
            "execution_model": "DETERMINISTIC_VIRTUAL_INTERPOLATION",
        }
