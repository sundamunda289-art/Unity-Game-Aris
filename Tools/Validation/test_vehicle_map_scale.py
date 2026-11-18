#!/usr/bin/env python3
"""Vehicle metres must agree with the actual 8 m map roads.
Static guards supplement native Unity prefab measurements, not replace them.
"""
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()
POLICY = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleVisualCalibration.cs").read_text()
MAP = json.loads((ROOT / "Docs/MAP_LAYOUT.json").read_text())
EXPECTED = {
    "Sedan": (1.96, 4.50),
    "SUV": (2.10, 4.80),
    "Sports": (1.99, 4.46),
    "GT": (2.08, 4.48),
    "Hatchback": (1.81, 3.90),
    "Van": (2.05, 5.08),
    "Utility": (2.10, 5.42),
    "Buggy": (1.90, 3.45),
}

class VehicleWorldScale(unittest.TestCase):
    def test_car_class_metre_envelopes_fit_two_lane_map(self):
        self.assertEqual(MAP["world_units"], "metres")
        self.assertEqual(MAP["size_m"], [2000, 2000])
        self.assertEqual(sorted({r["width"] for r in MAP["roads"]}), [7, 8])
        for car, (width, length) in EXPECTED.items():
            with self.subTest(car=car):
                literal = r'case "' + re.escape(car) + (
                    r'": return new Vector2\(([\d.]+)f, ([\d.]+)f\);')
                result = re.search(literal, POLICY)
                self.assertIsNotNone(result, car)
                self.assertEqual(tuple(float(x) for x in result.groups()),
                                 (width, length))
                self.assertLess(width, 7 / 2 - 1.2, car)
                self.assertLess(length, 6, car)

    def test_imported_z_up_cars_rotate_into_ground_plane_before_yaw(self):
        self.assertIn("Quaternion.Euler(-90f, 0f, 0f)", POLICY)
        self.assertIn("measuredSize.y > 2.75f", POLICY)
        build = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        i1 = build.index("CorrectSourceAxes(bounds.size)")
        i2 = build.index("bounds.size.x > bounds.size.z * 1.1f")
        i3 = build.index("FaceEmbeddedVehicleForward(")
        i4 = build.index("FittedScale(")
        self.assertLess(i1, i2)
        self.assertLess(i2, i3)
        self.assertLess(i3, i4)
        self.assertNotIn("1.88f / bounds.size.x", build)
        self.assertNotIn("4.28f / bounds.size.z", build)

    def test_undistorted_tyres_and_safe_fallback(self):
        self.assertIn('if (type == "GT")', POLICY)
        self.assertIn("new Vector3(width, wheelPlane, wheelPlane)", POLICY)
        self.assertIn("new Vector3(uniform, uniform, uniform)", POLICY)
        self.assertIn("float.IsNaN(measuredSize.sqrMagnitude)", POLICY)
        self.assertIn('FallBackToVerifiedModel(typeIndex, "FBX axis correction")', WORLD)
        self.assertIn("motor.AttachEmbeddedWheelModels(mesh.transform)", WORLD)
        self.assertIn("Vector3.Scale(mesh.transform.localScale, scale)", WORLD)

    def test_map_parking_clearance_matches_biggest_vehicles(self):
        probe = WORLD.split("private bool TryResolve(", 1)[1].split(
            "private bool TryResolvePlanned(", 1)[0]
        self.assertIn("FootprintMetres(record.type.id)", probe)
        self.assertIn("Mathf.Max(1.42f, footprint.y * .305f)", probe)
        self.assertIn("Mathf.Max(2.35f, footprint.y * .55f)", probe)
        self.assertIn("tallBody ? .94f : .64f", probe)
        self.assertIn("Physics.OverlapBoxNonAlloc(center, halfExtents,", probe)
        self.assertIn("HasSupport(check, y, out float localY)", probe)

if __name__ == "__main__":
    unittest.main()
