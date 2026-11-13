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
    # Increase offline candidate coverage without increasing live models or physics budgets.
    for xi in range(-18, 19):
        for zi in range(-18, 19):
            h = (((xi + 19) * 73856093) ^ ((zi + 23) * 19349663)) & 0xffffffff
            x = xi * 50 + ((h % 31) - 15)
            z = zi * 50 + (((h >> 8) % 31) - 15)
            if any(a[0] - 8 <= x <= a[2] + 8 and a[1] - 8 <= z <= a[3] + 8 for a in occupied):
                continue
            positions.append({"kind": "open", "x": x, "y": 0, "z": z, "origin": "grid", "slot": h & 65535})
    return {"schema": 1, "sizeMetres": 2000, "candidates": positions}

if __name__ == "__main__":
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(build(json.loads(SOURCE.read_text())), indent=2) + "\n")
    print("Baked candidates:", len(json.loads(OUTPUT.read_text())["candidates"]))
