#!/usr/bin/env python3
"""Deterministic, fail-closed *source* clearance simulation for enlarged houses.

The historical Docs/MAP_LAYOUT.json is a read-only baseline. Only garage-served
vehicle-spawn scene markers and their 2D asphalt aprons are eligible for
paired translations; fixed road exits, house transforms and native assets
are not silently modified. Real vehicle physics/colliders require Unity.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

HOUSE_FACTORS = {
    "a_frame_cabin": 1.20,
    "bungalow_house": 1.30,
    "pitched_roof_cottage": 1.18,
    "detached_two_storey_house": 1.12,
    "garage_apartment": 1.14,
    "two_bedroom_garage_house": 1.19,
    "row_three_townhouses": 1.10,
}
GARAGE_TYPES = frozenset(("garage_apartment", "two_bedroom_garage_house"))
SMALL_CLEARANCE_M = 2.0
MIN_SERVED_BAY_CLEARANCE_M = 0.25
MIN_REMAINING_ROAD_LINK_M = 5.0
EPS = 1e-6


class ClearanceError(ValueError):
    pass


def close(a, b, tolerance=EPS):
    return abs(float(a) - float(b)) <= tolerance


def box_gap(a, b):
    return math.hypot(max(0, a[0]-b[2], b[0]-a[2]),
                      max(0, a[1]-b[3], b[1]-a[3]))


def house_box(house, factor=1.0):
    a = house["aabb_xz_m"]
    x, z = (a[0]+a[2])/2, (a[1]+a[3])/2
    return [x+(a[0]-x)*factor, z+(a[1]-z)*factor,
            x+(a[2]-x)*factor, z+(a[3]-z)*factor]


def bay_box(spawn, xz=None):
    """Match original Tools/MapBuild/world_review.py:clearances exactly."""
    if spawn.get("reserved_size_m") != [6, 12]:
        raise ClearanceError("Unreviewed vehicle reserved bay dimensions")
    x, z = xz if xz is not None else (spawn["position_m"][0],spawn["position_m"][2])
    ex, ez = spawn["exit_world_xz_m"]
    return [min(x-3,ex-3), min(z-6,ez)-1,
            max(x+3,ex+3), max(z+6,ez)+1]


def simulation(layout, *, desired_house_scales=None):
    """Preview a replacement *target factor* for any named house, fail closed.

    Overrides are absolute multipliers against historical MAP_LAYOUT, not
    incremental Unity Transform scales. Do not apply the result without
    version-pinned scene/LFS transaction validation.
    """
    houses = layout["buildings"]
    spots = layout["vehicle_spawns"]
    by_name = {b["name"]:b for b in houses}
    overrides = desired_house_scales or {}
    if not isinstance(overrides,dict):
        raise ClearanceError("House scale overrides must be an object")
    for name,value in overrides.items():
        if (name not in by_name or by_name[name]["model"] not in HOUSE_FACTORS
            or type(value) not in (int,float) or not math.isfinite(value)
            or not 1.0 <= value <= 1.6):
            raise ClearanceError("Unreviewed house name or scale factor")
    def factor_for(house):
        return overrides.get(house["name"],HOUSE_FACTORS.get(house["model"],1.0))
    if len(by_name) != len(houses) or len({s["name"] for s in spots}) != len(spots):
        raise ClearanceError("Duplicate layout object names")
    rows = []
    planned = {}
    for spot in spots:
        p = spot["position_m"]
        x, z = p[0],p[2]
        x_new, z_new = x,z
        served = spot.get("serves")
        house = by_name.get(served)
        if served and house is None:
            raise ClearanceError("Reserved vehicle spawn has missing serving building")
        if house is not None and house["model"] in GARAGE_TYPES:
            if house["model"] not in HOUSE_FACTORS:
                raise ClearanceError("Unreviewed garage scale")
            hx, _, hz = house["position_m"]
            ex, ez = spot["exit_world_xz_m"]
            dx, dz = x-hx, z-hz
            # Permit only one straight, axis-aligned existing driveway
            # with the fixed exit beyond the point away from the building.
            if abs(dz) > EPS and abs(dx) <= EPS and abs(ex-x) <= EPS and (ez-z)*dz > 0:
                axis = "z"
                delta = (house["aabb_xz_m"][3]-house["aabb_xz_m"][1])/2
                shift = math.copysign(delta*(factor_for(house)-1), dz)
                z_new += shift
            elif abs(dx) > EPS and abs(dz) <= EPS and abs(ez-z) <= EPS and (ex-x)*dx > 0:
                axis = "x"
                delta = (house["aabb_xz_m"][2]-house["aabb_xz_m"][0])/2
                shift = math.copysign(delta*(factor_for(house)-1), dx)
                x_new += shift
            else:
                raise ClearanceError("Garage driveway is not straight and roadward: "+spot["name"])
            before = box_gap(bay_box(spot), house_box(house))
            after = box_gap(bay_box(spot, [x_new,z_new]),
                            house_box(house, factor_for(house)))
            if (before < MIN_SERVED_BAY_CLEARANCE_M - EPS or not close(before,after)
                or math.hypot(ex-x_new,ez-z_new) < MIN_REMAINING_ROAD_LINK_M):
                raise ClearanceError("Garage reservation loses original clearance/road access: "+spot["name"])
            rows.append({
                "spawn_name":spot["name"],"house_name":house["name"],
                "model":house["model"],"axis":axis,
                "before_clearance_m":before,"after_clearance_m":after,
                "original_position_m":list(p),
                "new_position_m":[x_new,p[1],z_new],
                "delta_xz_m":[x_new-x,z_new-z],
                "road_exit_xz_m":list(spot["exit_world_xz_m"]),
            })
        planned[spot["name"]] = [x_new,z_new]

    # Every 2D vehicle reservation, moved or not, is tested against every
    # residential and non-residential building, not merely the serving house.
    # Original gap < 2m must never decrease; otherwise remain >=2m.
    for spot in spots:
        before_bay = bay_box(spot)
        after_bay = bay_box(spot,planned[spot["name"]])
        for house in houses:
            before = box_gap(before_bay, house_box(house))
            after = box_gap(after_bay, house_box(
                house,factor_for(house)))
            if before + EPS < MIN_SERVED_BAY_CLEARANCE_M:
                raise ClearanceError("Existing spawn already intrudes into building: "+
                                     spot["name"]+" -> "+house["name"])
            if after + EPS < MIN_SERVED_BAY_CLEARANCE_M:
                raise ClearanceError("Enlarged building intrudes into vehicle reservation: "+
                                     spot["name"]+" -> "+house["name"])
            if after + EPS < min(before,SMALL_CLEARANCE_M):
                raise ClearanceError("New reservation/building conflict: "+
                                     spot["name"]+" -> "+house["name"])
    # Detect new vehicle-pad vs pad overlaps; already shared road/path
    # corridors can be intentionally shared, so compare centers of the
    # 6x12 reserved *parking pads*, not the whole exit corridors.
    def pad(s, p):
        hx,hz = s["reserved_size_m"][0]/2,s["reserved_size_m"][1]/2
        return [p[0]-hx,p[1]-hz,p[0]+hx,p[1]+hz]
    for i, spot in enumerate(spots):
        for other in spots[i+1:]:
            oldgap=box_gap(pad(spot,[spot["position_m"][0],spot["position_m"][2]]),
                           pad(other,[other["position_m"][0],other["position_m"][2]]))
            newgap=box_gap(pad(spot,planned[spot["name"]]),
                           pad(other,planned[other["name"]]))
            if newgap+EPS < min(oldgap,SMALL_CLEARANCE_M):
                raise ClearanceError("Vehicle parking pads became too close: "+
                                     spot["name"]+" -> "+other["name"])
    return {
        "total_buildings":len(houses),"total_spawns":len(spots),
        "per_house_scale_overrides":dict(sorted(overrides.items())),
        "relocated":sorted(rows,key=lambda r:r["spawn_name"]),
        "unchanged_spawns":len(spots)-len(rows),
        "checks":"all marker vs all building saved AABBs; all marker pad pairs; unchanged road exits",
        "evidence":"2D serialized-layout only; not native TerrainData, vehicle collider, pathfinding, slope, or physics",
    }


def native_obstacle_audit(root, layout, *, safety_distance_m=3.0, desired_house_scales=None):
    """Decode true native LFS centers; flag rocks/trees for bounded review.

    Do not move without measured prefab collision radius, slope and a verified
    Unity terrain edit/readback: TreeInstance can be broader than its pivot.
    """
    from terrain import NativeTerrain
    from unity_data import World
    world=World(root)
    native=NativeTerrain(world)
    plan=simulation(layout, desired_house_scales=desired_house_scales)
    factors=desired_house_scales or {}
    bounds=[(b,house_box(b),house_box(b,factors.get(b["name"],HOUSE_FACTORS[b["model"]])))
            for b in layout["buildings"] if b["model"] in HOUSE_FACTORS]
    by_category={"rock":0,"tree":0,"shrub":0,"unresolved":0}
    alerts=[]
    spots={s["name"]:s for s in layout["vehicle_spawns"]}
    roadward=[]
    for row in plan["relocated"]:
        spot=spots[row["spawn_name"]]
        roadward.append((row["spawn_name"],bay_box(spot),bay_box(spot,
                         [row["new_position_m"][0],row["new_position_m"][2]])))
    route_obstacles=[]
    new_route_obstacles=[]
    for item in native.instances():
        category=item.get("category","unresolved")
        by_category[category]=by_category.get(category,0)+1
        if category not in ("rock","tree","shrub"):
            continue
        x,_,z=item["position_m"]
        point=[x,z,x,z]
        for route,old_bay,new_bay in roadward:
            before=box_gap(point,old_bay)
            after=box_gap(point,new_bay)
            if after < 2.5:
                incident={"route":route,"instance_id":item["id"],"category":category,
                          "center_to_route_m":round(after,3),
                          "previous_center_to_route_m":round(before,3),
                          "prototype_path":item.get("path")}
                route_obstacles.append(incident)
                if before >= 2.5:
                    new_route_obstacles.append(incident)
        for house,old,new in bounds:
            before=box_gap(point,old)
            after=box_gap(point,new)
            if after >= safety_distance_m and before >= safety_distance_m:
                continue
            alerts.append({
                "instance_id":item["id"],"category":category,
                "house":house["name"],
                "prototype_path":item.get("path"),
                "position_xz_m":[round(x,3),round(z,3)],
                "previous_center_to_house_footprint_m":round(before,3),
                "enlarged_center_to_house_footprint_m":round(after,3),
                "loss_of_center_clearance_m":round(max(0,before-after),3),
                "action":"requires_prototype_bounds_and_Unity_physics_before_any_move",
            })
    alerts.sort(key=lambda a:(a["enlarged_center_to_house_footprint_m"],a["house"],a["instance_id"]))
    # Sample the real terrain in a 4.8m-wide drivable corridor, not
    # just the parked vehicle center. This is a grid/slope proxy, not a
    # 3D Collider/Rigidbody or mesh ground-contact result.
    ground_routes=[]
    for row in plan["relocated"]:
        p=row["new_position_m"]
        start=(p[0],p[2])
        finish=tuple(row["road_exit_xz_m"])
        dx,dz=finish[0]-start[0],finish[1]-start[1]
        length=math.hypot(dx,dz)
        if length < MIN_REMAINING_ROAD_LINK_M:
            raise ClearanceError("Road exit is too near after garage adjustment")
        nx,nz=-dz/length,dx/length
        n_steps=max(2,math.ceil(length/2))
        steep=0
        worst_cross=0
        max_vertical_offset=0
        max_seam=0
        for k in range(n_steps+1):
            t=k/n_steps
            px,pz=start[0]+t*dx,start[1]+t*dz
            elevations=[]
            for lateral in (-2.4,0,2.4):
                value=native.height(px+lateral*nx,pz+lateral*nz)
                max_seam=max(max_seam,value["seam_height_delta_m"])
                records=value["samples"]
                sample=records[0]
                elevations.append(sample["height_m"])
                steep=max(steep,max(rec["slope_degrees"] for rec in records))
                max_vertical_offset=max(max_vertical_offset,
                                        abs(sample["height_m"]-p[1]))
            worst_cross=max(worst_cross,max(elevations)-min(elevations))
        ground_routes.append({"spawn":row["spawn_name"],"road_link_length_m":round(length,3),
            "max_native_slope_degrees":round(steep,3),
            "max_lateral_terrain_height_spread_m":round(worst_cross,3),
            "max_terrain_vs_saved_spawn_y_m":round(max_vertical_offset,3),
            "max_seam_discontinuity_m":round(max_seam,4),
            "requires_unity_physics_probe":True})
    return {
        "native_instances_by_category":by_category,
        "review_radius_m":safety_distance_m,
        "near_residential_instance_pair_count":len(alerts),
        "nearby_pairs":alerts[:40],
        "garage_routes_with_native_centers_within_2_5m":len(route_obstacles),
        "newly_intersected_route_centers":len(new_route_obstacles),
        "garage_route_native_center_samples":route_obstacles[:30],
        "new_route_obstacle_samples":new_route_obstacles[:30],
        "native_terrain_driveway_profiles":ground_routes,
        "full_pair_count":len(alerts),
        "safe_auto_move_count":0,
        "warning":"Positions are native TerrainData instance centers, not exact collider extents. No rock/tree moved and no physics claim.",
    }


def main():
    p=argparse.ArgumentParser()
    p.add_argument("--root",type=Path,default=Path(__file__).resolve().parents[2])
    p.add_argument("--native",action="store_true",help="Requires hydrated Unity TerrainData Git LFS")
    p.add_argument("--house-scales-json",type=Path,help="Optional JSON object of {exact house name: target absolute scale} for read-only prediction")
    args=p.parse_args()
    layout=json.loads((args.root/"Docs/MAP_LAYOUT.json").read_text())
    overrides=json.loads(args.house_scales_json.read_text()) if args.house_scales_json else None
    result=simulation(layout, desired_house_scales=overrides)
    if args.native:
        result["native_obstacles"]=native_obstacle_audit(args.root,layout,
                                                          desired_house_scales=overrides)
    print("AARIS_RESIDENTIAL_CLEARANCE_SIMULATION="+json.dumps(result,separators=(",",":")))
    return 0


if __name__=="__main__":
    raise SystemExit(main())
