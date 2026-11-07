"""Conservative deterministic *virtual* fleet task allocator.

No airspace, terrain, obstacle, wind, BVLOS, battery health or regulatory
validation is claimed. Every result is simulator-only and operator-provided.
"""
from __future__ import annotations

import hashlib
import json
import math
from pydantic import BaseModel, ConfigDict, Field, model_validator


class VehicleProfile(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    home_lat: float = Field(ge=-85, le=85)
    home_lon: float = Field(ge=-180, le=180)
    energy_wh: float = Field(gt=0, le=5000)
    reserve_fraction: float = Field(ge=0.25, le=0.75)
    energy_wh_per_km: float = Field(gt=0, le=500)


class TaskSpec(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    key: str = Field(min_length=3, max_length=64, pattern=r"^[A-Za-z0-9_-]+$")
    latitude: float = Field(ge=-85, le=85)
    longitude: float = Field(ge=-180, le=180)
    relative_altitude_m: float = Field(ge=10, le=120)
    required_energy_wh: float = Field(ge=0, le=2000)
    priority: int = Field(ge=0, le=10)


class MissionSpec(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    request_id: str = Field(pattern=r"^[0-9a-f]{32}$")
    vehicle_ids: list[str] = Field(min_length=1, max_length=8)
    tasks: list[TaskSpec] = Field(min_length=1, max_length=64)

    @model_validator(mode="after")
    def unique_fields(self):
        if len(set(self.vehicle_ids)) != len(self.vehicle_ids):
            raise ValueError("Duplicate vehicle ID")
        if len({task.key for task in self.tasks}) != len(self.tasks):
            raise ValueError("Duplicate task ID")
        return self


def digest(data: dict) -> str:
    canonical = json.dumps(data, sort_keys=True, separators=(",", ":"),
                           allow_nan=False).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()


def distance_m(a: tuple[float, float], b: tuple[float, float]) -> float:
    p1, p2 = math.radians(a[0]), math.radians(b[0])
    dp = p2 - p1
    dl = math.radians(b[1] - a[1])
    h = math.sin(dp / 2) ** 2 + math.cos(p1) * math.cos(p2) * math.sin(dl / 2) ** 2
    return 12742000 * math.asin(min(1, math.sqrt(max(0, h))))


def allocate(spec: MissionSpec, vehicles: dict[str, VehicleProfile]) -> list[dict]:
    remaining = {name: v.energy_wh for name, v in vehicles.items()}
    result = []
    for task in sorted(spec.tasks, key=lambda x: (-x.priority, x.key)):
        candidates = []
        for ident, vehicle in sorted(vehicles.items()):
            d = distance_m((vehicle.home_lat, vehicle.home_lon),
                           (task.latitude, task.longitude))
            if d > 2000:  # planning envelope only; not approved geofence
                continue
            estimate = 2 * d / 1000 * vehicle.energy_wh_per_km + task.required_energy_wh
            reserve = vehicle.energy_wh * vehicle.reserve_fraction
            if remaining[ident] - estimate >= reserve:
                candidates.append((estimate, d, ident))
        if not candidates:
            raise ValueError("Fleet task unassignable under provided energy/range assumptions")
        energy, dist, vehicle = min(candidates)
        remaining[vehicle] -= energy
        result.append({
            "task_key": task.key, "vehicle_id": vehicle,
            "estimated_wh": round(energy, 3),
            "round_trip_m": round(2 * dist, 2),
            "task": task.model_dump(mode="json"),
        })
    return result
