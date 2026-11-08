#!/usr/bin/env python3
"""Regression gates for drivable map geometry and AI-readable vehicle exits.

License-free tests against the committed authored blueprint. These do not
claim native Unity TerrainData / URP render validation.
"""
import copy
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Tools" / "CI"))
from map_static_check import check, inspect


class MapSafetyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.layout = json.loads((ROOT / "Docs" / "MAP_LAYOUT.json").read_text())

    def broken_layout(self):
        return copy.deepcopy(self.layout)

    def test_real_blueprint_has_safe_drive_outs(self):
        result = check(self.layout)
        self.assertEqual(result["result"], "passed")
        self.assertTrue(result["checks"]["vehicle_exits_connect_to_roads"]["passed"])
        inspection = inspect(self.layout, -420, -210)
        for spawn in inspection["vehicle_spawns"]:
            self.assertLessEqual(spawn["road_exit_distance_m"], 1.0)
            self.assertEqual(len(spawn["exit_world_xz_m"]), 2)

    def test_rejects_empty_centerline(self):
        layout = self.broken_layout()
        layout["roads"][0]["points"] = []
        with self.assertRaisesRegex(AssertionError, "road_centerlines_drivable"):
            check(layout)

    def test_rejects_duplicate_consecutive_road_points(self):
        layout = self.broken_layout()
        layout["roads"][0]["points"][1] = layout["roads"][0]["points"][0][:]
        with self.assertRaisesRegex(AssertionError, "road_centerlines_drivable"):
            check(layout)

    def test_rejects_nan_road_elevation(self):
        layout = self.broken_layout()
        layout["roads"][0]["points"][0][1] = float("nan")
        with self.assertRaisesRegex(AssertionError, "road_centerlines_drivable"):
            check(layout)

    def test_rejects_infinite_house_scale_geometry(self):
        layout = self.broken_layout()
        layout["buildings"][0]["world_dimensions_m"][0] = float("inf")
        with self.assertRaisesRegex(AssertionError, "building_geometry_finite"):
            check(layout)

    def test_rejects_nonfinite_vehicle_position(self):
        layout = self.broken_layout()
        layout["vehicle_spawns"][0]["position_m"][0] = float("nan")
        with self.assertRaisesRegex(AssertionError, "vehicle_spawn_geometry_finite"):
            check(layout)

    def test_rejects_vehicle_exit_disconnected_from_any_road(self):
        layout = self.broken_layout()
        layout["vehicle_spawns"][0]["exit_world_xz_m"] = [999.0, 999.0]
        with self.assertRaisesRegex(AssertionError, "vehicle_exits_connect_to_roads"):
            check(layout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
