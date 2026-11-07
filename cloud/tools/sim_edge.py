"""Cloud Director's workflow-only simulated device client.

This is NOT a vehicle flight simulator or flight command gateway. It consumes
only explicitly virtual job receipts and produces simulated acknowledgments.
Never imports pymavlink, serial, DroneKit or Mission Planner.
"""
from __future__ import annotations

import time
import uuid

from cloud.app.planner import TaskSpec


class VirtualEdge:
    def __init__(self, http_client, *, device_id: str, bearer: str):
        if not device_id.startswith(("SIM-", "AH-SIM-")):
            raise ValueError("Refuse non-virtual device namespace")
        if not bearer:
            raise ValueError("Device identity required")
        self.client = http_client
        self.device_id = device_id
        self.headers = {"Authorization": "Bearer " + bearer}

    def poll_once(self, *, processing_seconds: float = 0) -> dict:
        if not 0 <= processing_seconds <= 0.5:
            raise ValueError("Simulated workflow delay is out of bounds")
        reply = self.client.post("/api/v1/device/jobs/claim",
                                 headers=self.headers, json={})
        reply.raise_for_status()
        job = reply.json()["job"]
        if job is None:
            return {"status": "IDLE_SIM"}
        if (job.get("execution_target") != "VIRTUAL_SIMULATOR_ONLY" or
                job.get("physical_command_authorized") is not False or
                job.get("lease_epoch", 0) < 1):
            raise ValueError("Unsafe job envelope: not a virtual workload")
        task = TaskSpec.model_validate(job["payload"])
        if task.key != job["task_key"]:
            raise ValueError("Task identity mismatch")

        def ack(phase: str):
            response = self.client.post(
                "/api/v1/device/jobs/" + job["id"] + "/ack",
                headers=self.headers, json={
                    "event_id": uuid.uuid4().hex,
                    "claim_token": job["claim_token"], "phase": phase})
            response.raise_for_status()
            return response.json()

        ack("ACCEPTED_SIM")
        if processing_seconds:
            time.sleep(processing_seconds)
        result = ack("COMPLETED_SIM")
        return {"status": result["status"], "job_id": job["id"],
                "flight_was_performed": False}
