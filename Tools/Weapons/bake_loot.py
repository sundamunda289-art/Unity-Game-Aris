#!/usr/bin/env python3
"""Bake cheap, deterministic authored loot candidates from existing map JSON.

This script does NOT guess that a mesh is a house from a collider's size.
Each authored building's AABB supplies a bounded candidate footprint.
Unity verifies true reachable floor/roof/clearance at stream time.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "Docs/MAP_LAYOUT.json"
OUTPUT = ROOT / "Assets/Resources/AarisWeapons/loot_candidates.json"

def build(layout):
    all_buildings = layout["buildings"]
    positions = []
    occupied = [b["aabb_xz_m"] for b in all_buildings]
    interior_models = ("house", "cottage", "cabin", "bungalow", "apartment", "villa", "residential")
    for building in all_buildings:
        if not any(word in building["model"].lower() for word in interior_models):
            continue
        a = building["aabb_xz_m"]
        mid_x, mid_z = (a[0] + a[2]) * .5, (a[1] + a[3]) * .5
        extent_x, extent_z = (a[2] - a[0]) * .5, (a[3] - a[1]) * .5
        for i, (px, pz) in enumerate(((0, 0), (-.48, .25), (.35, -.28))):
            positions.append({"kind": "indoor", "x": round(mid_x + px * extent_x, 3),
                              "y": round(building["ground_y_m"], 3),
                              "z": round(mid_z + pz * extent_z, 3),
                              "origin": building["name"], "slot": i})
    for xi in range(-9, 10):
        for zi in range(-9, 10):
            h = (((xi + 19) * 73856093) ^ ((zi + 23) * 19349663)) & 0xffffffff
            x = xi * 100 + ((h % 71) - 35)
            z = zi * 100 + (((h >> 8) % 71) - 35)
            if any(a[0] - 8 <= x <= a[2] + 8 and a[1] - 8 <= z <= a[3] + 8 for a in occupied):
                continue
            positions.append({"kind": "open", "x": x, "y": 0, "z": z, "origin": "grid", "slot": h & 65535})
    # Preserve all existing map loot indices. Satellites fill gaps between
    # 100 m outdoor cells, without increasing runtime mesh/physics budgets.
    # Ground height, collision, roofs and water are validated at stream time.
    original_outdoor = tuple(p for p in positions if p["kind"] == "open")
    for index, center in enumerate(original_outdoor):
        for side, (dx, dz) in enumerate(((24, 24), (-24, -24))):
            x, z = center["x"] + dx, center["z"] + dz
            if not (-957 <= x <= 957 and -957 <= z <= 957):
                continue
            if any(a[0] - 8 <= x <= a[2] + 8 and a[1] - 8 <= z <= a[3] + 8 for a in occupied):
                continue
            # Avoid piles of pickups from neighboring jittered cells.
            if any((p["x"] - x) ** 2 + (p["z"] - z) ** 2 < 14 ** 2
                   for p in positions if p["kind"] == "open"):
                continue
            positions.append({"kind": "open", "x": x, "y": 0, "z": z,
                              "origin": "grid", "slot": 100000 + index * 2 + side})
    # Coverage audit: on a 50m lattice, 166/1521 sampled locations had no
    # authored outdoor point within a 50m stream radius. Add only missing
    # sectors as candidate LOCATIONS, not immediately instantiated FBX meshes.
    # Keep every old candidate index unchanged and validate each new spot on
    # native Terrain + real collision when the player comes near it.
    # A 50m grid with <=6m offset avoids visual lines of guns.
    for xi in range(-19, 20):
        for zi in range(-19, 20):
            h = (((xi + 31) * 73856093) ^ ((zi + 47) * 19349663)) & 0xffffffff
            x = xi * 50 + ((h % 13) - 6)
            z = zi * 50 + (((h >> 8) % 13) - 6)
            if any(a[0] - 8 <= x <= a[2] + 8 and
                   a[1] - 8 <= z <= a[3] + 8 for a in occupied):
                continue
            if any(p["kind"] == "open" and
                   (p["x"] - x) ** 2 + (p["z"] - z) ** 2 < 40 ** 2
                   for p in positions):
                continue
            positions.append({"kind": "open", "x": x, "y": 0, "z": z,
                              "origin": "grid",
                              "slot": 200000 + (xi + 19) * 39 + (zi + 19)})

    # Finish the last three 50m sample-grid gaps without renumbering
    # existing loot or raising the 40-live-mesh mobile budget.
    # These are authored choices only. Unity terrain, walls, roof and slope
    # physics must still validate each location during nearby streaming.
    last_mile_slot = 300000
    for gx in range(-925, 926, 50):
        for gz in range(-925, 926, 50):
            if any(p["kind"] == "open" and
                   (p["x"] - gx) ** 2 + (p["z"] - gz) ** 2 <= 50 ** 2
                   for p in positions):
                continue
            for dx, dz in ((12, -10), (-12, 10), (0, 0),
                           (12, 10), (-12, -10), (24, 0), (0, -24)):
                x, z = gx + dx, gz + dz
                if any(a[0]-8 <= x <= a[2]+8 and a[1]-8 <= z <= a[3]+8
                       for a in occupied):
                    continue
                if any(p["kind"] == "open" and
                       (p["x"]-x)**2 + (p["z"]-z)**2 < 14**2
                       for p in positions):
                    continue
                positions.append({"kind":"open","x":x,"y":0,"z":z,
                                  "origin":"grid","slot":last_mile_slot})
                last_mile_slot += 1
                break
    return {"schema": 1, "sizeMetres": 2000, "candidates": positions}

if __name__ == "__main__":
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(build(json.loads(SOURCE.read_text())), indent=2) + "\n")
    print("Baked candidates:", len(json.loads(OUTPUT.read_text())["candidates"]))
