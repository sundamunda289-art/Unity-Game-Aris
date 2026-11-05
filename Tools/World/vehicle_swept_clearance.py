#!/usr/bin/env python3
"""Read-only 2D swept car-envelope preflight for residential parking routes.

The actual Unity vehicle prefab, WheelColliders, terrain mesh, navigation, and
steering are not inferred from planning markers. Dimensions here are explicit,
configurable *test envelopes*, not measured models. This is not physics QA.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from smart_residential_clearance import (
    ClearanceError, EPS, HOUSE_FACTORS, box_gap, house_box, simulation,
)

DEFAULT_WIDTH_M = 2.2
DEFAULT_LENGTH_M = 5.0
DEFAULT_MARGIN_M = 0.5


def _positive(label: str, value: float) -> float:
    if type(value) not in (float, int) or not math.isfinite(value) or value <= 0:
        raise ClearanceError(f"Vehicle {label} must be finite and positive")
    return float(value)


def swept_aabb(position_m, road_exit_xz_m, *, width_m=DEFAULT_WIDTH_M,
               length_m=DEFAULT_LENGTH_M, margin_m=DEFAULT_MARGIN_M):
    """Axis-aligned Minkowski envelope of a straight, fixed-heading car.

    The center moves from the marker to the exit without rotation. Its full
    length/width, plus the specified safety margin, are swept over that path.
    Diagonal paths are deliberately unsupported rather than approximated.
    """
    width = _positive("width", width_m)
    length = _positive("length", length_m)
    if type(margin_m) not in (float, int) or not math.isfinite(margin_m) or margin_m < 0:
        raise ClearanceError("Vehicle margin must be finite and nonnegative")
    if len(position_m) != 3 or len(road_exit_xz_m) != 2:
        raise ClearanceError("Vehicle marker requires XYZ and road exit XZ")
    x, z = float(position_m[0]), float(position_m[2])
    ex, ez = map(float, road_exit_xz_m)
    if not all(math.isfinite(n) for n in (x,z,ex,ez)):
        raise ClearanceError("Nonfinite vehicle route coordinate")
    dx, dz = ex-x, ez-z
    side = width/2 + margin_m
    front = length/2 + margin_m
    if abs(dx) <= EPS and abs(dz) > EPS:
        return [x-side, min(z,ez)-front, x+side, max(z,ez)+front]
    if abs(dz) <= EPS and abs(dx) > EPS:
        return [min(x,ex)-front, z-side, max(x,ex)+front, z+side]
    raise ClearanceError("Car sweep supports only nonzero, axis-aligned straight routes")


def audit(layout, *, desired_house_scales=None, vehicle_width_m=DEFAULT_WIDTH_M,
          vehicle_length_m=DEFAULT_LENGTH_M, vehicle_margin_m=DEFAULT_MARGIN_M):
    """All 45 parking-to-road straight sweeps against all 59 buildings.

    Reject any intersected building AABB on the updated map. This catches
    collisions that a point-marker check could miss, but never claims that a
    real vehicle can steer/fit over native Unity colliders.
    """
    plan = simulation(layout, desired_house_scales=desired_house_scales)
    changes = {row["spawn_name"]: row for row in plan["relocated"]}
    if len(changes) != len(plan["relocated"]):
        raise ClearanceError("Duplicate relocated spawn")
    factors = desired_house_scales or {}
    expanded = {h["name"]: house_box(h, factors.get(
        h["name"], HOUSE_FACTORS.get(h["model"], 1.0)))
        for h in layout["buildings"]}
    rows = []
    failures = []
    for spawn in layout["vehicle_spawns"]:
        record = changes.get(spawn["name"])
        after = record["new_position_m"] if record else spawn["position_m"]
        footprint = swept_aabb(after, spawn["exit_world_xz_m"],
            width_m=vehicle_width_m, length_m=vehicle_length_m,
            margin_m=vehicle_margin_m)
        distances = sorted(((box_gap(footprint, region), name)
                            for name, region in expanded.items()))
        if not distances:
            raise ClearanceError("No buildings in tested layout")
        for distance, building in distances:
            if distance <= EPS:
                failures.append({"spawn":spawn["name"],"building":building})
        rows.append({"spawn":spawn["name"],
                     "relocated":bool(record),
                     "road_exit_xz_m":list(spawn["exit_world_xz_m"]),
                     "tested_vehicle_swept_aabb_xz_m":[round(v,5) for v in footprint],
                     "nearest_building":distances[0][1],
                     "clearance_after_envelope_m":round(distances[0][0],4)})
    if failures:
        raise ClearanceError("Swept vehicle envelope intersects building: " +
                             json.dumps(failures[:8],separators=(",",":")))
    return {
        "status":"2d_source_envelopes_clear",
        "tested_spawns":len(rows),
        "relocated_garage_spawns":len(changes),
        "tested_buildings":len(expanded),
        "vehicle_envelope_width_m":vehicle_width_m,
        "vehicle_envelope_length_m":vehicle_length_m,
        "vehicle_envelope_edge_margin_m":vehicle_margin_m,
        "minimum_envelope_to_building_gap_m":min(r["clearance_after_envelope_m"] for r in rows),
        "routes":rows,
        "limits":"Static straight-line XZ AABBs only. Vehicle meshes, steering sweep, turning radius, terrain contact, dynamic colliders, Unity physics, actual screenshots and Android tests unverified.",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--house-scales-json", type=Path,
                        help="Optional reviewed absolute replacement scale factors")
    parser.add_argument("--vehicle-width-m", type=float, default=DEFAULT_WIDTH_M)
    parser.add_argument("--vehicle-length-m", type=float, default=DEFAULT_LENGTH_M)
    parser.add_argument("--vehicle-margin-m", type=float, default=DEFAULT_MARGIN_M)
    args = parser.parse_args()
    layout = json.loads((args.root/"Docs/MAP_LAYOUT.json").read_text())
    scales = json.loads(args.house_scales_json.read_text()) if args.house_scales_json else None
    result = audit(layout, desired_house_scales=scales,
                   vehicle_width_m=args.vehicle_width_m,
                   vehicle_length_m=args.vehicle_length_m,
                   vehicle_margin_m=args.vehicle_margin_m)
    print("AARIS_SWEPT_VEHICLE_AUDIT="+json.dumps(result,separators=(",",":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
