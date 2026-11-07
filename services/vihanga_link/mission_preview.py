"""Deterministic, non-actuating cloud-assisted mission preview.

Only computes route geometry, bounded simulated timing and camera counts.
No mission upload, arming, MAVLink generation or hardware commands.
"""
from __future__ import annotations

import hashlib
import json
import math

from pydantic import BaseModel, ConfigDict, Field, model_validator

EARTH_RADIUS_M = 6371008.8
MAX_WAYPOINTS = 128
MAX_DISTANCE_M = 25000.0


class Point(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    lat: float = Field(ge=-90.0, le=90.0)
    lon: float = Field(ge=-180.0, le=180.0)
    altitude_m: float = Field(ge=5.0, le=120.0)


class Boundary(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    min_lat: float = Field(ge=-90, le=90)
    max_lat: float = Field(ge=-90, le=90)
    min_lon: float = Field(ge=-180, le=180)
    max_lon: float = Field(ge=-180, le=180)
    min_altitude_m: float = Field(ge=0, le=120)
    max_altitude_m: float = Field(ge=1, le=120)

    @model_validator(mode="after")
    def ordered(self):
        if not (self.min_lat < self.max_lat and
                self.min_lon < self.max_lon and
                self.min_altitude_m < self.max_altitude_m):
            raise ValueError("Survey bounds must form a nonempty rectangular volume")
        if self.max_lon - self.min_lon > 1:
            raise ValueError("Cross-dateline or oversized longitude regions need explicit handling")
        return self

    def contains(self, point: Point) -> bool:
        return (self.min_lat <= point.lat <= self.max_lat and
                self.min_lon <= point.lon <= self.max_lon and
                self.min_altitude_m <= point.altitude_m <= self.max_altitude_m)


class PreviewRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", allow_inf_nan=False)
    vehicle_id: str = Field(min_length=3, max_length=64, pattern=r"^[A-Za-z0-9_-]+$")
    route: list[Point] = Field(min_length=2, max_length=MAX_WAYPOINTS)
    bounds: Boundary
    speed_mps: float = Field(ge=0.5, le=15)
    estimated_max_flight_seconds: int = Field(ge=60, le=3600)
    reserve_percent: float = Field(ge=10, le=70)
    camera_interval_seconds: float = Field(default=2, ge=0.5, le=60)

    @model_validator(mode="after")
    def in_bounds(self):
        if any(not self.bounds.contains(point) for point in self.route):
            raise ValueError("Route escapes the declared geofence volume")
        return self


def haversine_m(a: Point, b: Point) -> float:
    lat1 = math.radians(a.lat)
    lat2 = math.radians(b.lat)
    delta_lat = lat2 - lat1
    delta_lon = math.radians(b.lon - a.lon)
    half = (math.sin(delta_lat / 2.0) ** 2 +
            math.cos(lat1) * math.cos(lat2) *
            math.sin(delta_lon / 2.0) ** 2)
    return 2 * EARTH_RADIUS_M * math.asin(min(1.0, math.sqrt(half)))


def preview(request: PreviewRequest) -> dict:
    """Never creates MAVLink bytes or schedules a physical operation."""
    total_m = 0.0
    total_s = 0.0
    timeline = [{"index": 0, "eta_seconds": 0.0,
                 "lat": request.route[0].lat, "lon": request.route[0].lon,
                 "altitude_m": request.route[0].altitude_m}]
    for index, (a, b) in enumerate(
            zip(request.route, request.route[1:]), start=1):
        horizontal = haversine_m(a, b)
        climb_seconds = abs(a.altitude_m - b.altitude_m) / 2.0
        # Max of horizontal and vertical transit gives a lower bound for
        # simultaneous movement. This is a geometric preview, not dynamics.
        leg_seconds = max(horizontal / request.speed_mps, climb_seconds)
        if leg_seconds < 0.001:
            raise ValueError("Consecutive waypoints must be distinct")
        total_m += horizontal
        total_s += leg_seconds
        if total_m > MAX_DISTANCE_M:
            raise ValueError("Route exceeds offline preview distance limit")
        timeline.append({"index": index,
                         "eta_seconds": round(total_s, 2),
                         "lat": b.lat, "lon": b.lon,
                         "altitude_m": b.altitude_m})

    # Treat declared maximum endurance as a user-supplied *assumption*;
    # no telemetry-backed energy reserve is asserted by this preview.
    permitted_seconds = (
        request.estimated_max_flight_seconds *
        (1.0 - request.reserve_percent / 100.0))
    if total_s > permitted_seconds:
        raise ValueError("Route exceeds operator-declared reserve-adjusted endurance")

    canonical = json.dumps(request.model_dump(mode="json"),
                           sort_keys=True, separators=(",", ":"), allow_nan=False)
    digest = hashlib.sha256(canonical.encode("utf-8")).hexdigest()
    return {
        "status": "SIMULATION_PREVIEW_ONLY",
        "vehicle_id": request.vehicle_id,
        "revision_sha256": digest,
        "waypoints": len(request.route),
        "horizontal_distance_m": round(total_m, 2),
        "estimated_seconds": round(total_s, 2),
        "camera_capture_estimate": int(math.floor(
            total_s / request.camera_interval_seconds)) + 1,
        "declared_reserve_percent": request.reserve_percent,
        "declared_endurance_seconds": request.estimated_max_flight_seconds,
        "timeline": timeline,
        "limitations": [
            "No wind, battery telemetry, GNSS uncertainty or dynamics modeled",
            "No vehicle or mission commands generated",
            "Geofence volume is operator-declared and not FC geofence evidence"
        ],
        "vehicle_command_authorized": False,
    }
