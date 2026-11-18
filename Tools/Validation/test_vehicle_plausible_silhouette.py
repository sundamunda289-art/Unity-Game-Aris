#!/usr/bin/env python3
"""Require physically scaled visual car silhouettes; no toy 30-cm imports.

Source/geometry regression only. Native Unity/Android rendering still needed.
"""
import json
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()
CATALOG = json.loads(
    (ROOT / "Assets/Resources/AarisVehicles/vehicle_catalog.json").read_text()
)


def plausible(size, width, length):
    x, y, z = size
    if not all(math.isfinite(n) for n in (x, y, z, width, length)):
        return False
    if width <= 0 or length <= 0:
        return False
    return (width * .74 <= x <= width * 1.08 and
            length * .78 <= z <= length * 1.08 and
            .82 <= y <= 2.58 and
            z * .28 <= x <= z * .72)


class PhysicalVehicleSilhouette(unittest.TestCase):
    def test_high_fidelity_art_must_fit_each_car_class_before_physics(self):
        build = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        guard = build.index("HasPlausibleAuthoredSilhouette(")
        self.assertIn("record.type.useEmbeddedAuthoredWheels ||", build)
        self.assertIn("record.type.preserveAuthoredUrpMaterials ||", build)
        self.assertIn("record.type.convertAuthoredBodyMaterials;", build)
        self.assertIn('FallBackToVerifiedModel(typeIndex, "unrealistic car silhouette")', build)
        self.assertLess(build.index("AlignEmbeddedTyresToRoad("), guard)
        self.assertLess(guard, build.index("var motor = root.AddComponent<AarisVehicleMotor>()"))
        self.assertLess(guard, build.index("record.visible = motor;"))
        self.assertIn("if (importedCar && !HasPlausibleAuthoredSilhouette(", build)

    def test_class_based_limits_include_sports_hatchback_van_and_buggy(self):
        for car in CATALOG["vehicles"]:
            width = car["referenceWidthMetres"]
            length = car["referenceLengthMetres"]
            for scale in (.81, .92, 1.00):
                height = 1.20 if car["id"] in ("GT", "Sports") else 1.64
                self.assertTrue(plausible(
                    (width * scale, height, length * scale),
                    width, length), car["id"])
            self.assertFalse(plausible((width * .20, 1.2, length), width, length))
            self.assertFalse(plausible((width, 1.2, length * .20), width, length))
            self.assertFalse(plausible((width, .31, length), width, length))
            self.assertFalse(plausible((width, 3.20, length), width, length))
            self.assertFalse(plausible((width * 1.38, 1.7, length), width, length))
            self.assertFalse(plausible((width, 1.6, length * 1.32), width, length))
        self.assertFalse(plausible((math.nan, 1.4, 4), 2, 4))
        self.assertFalse(plausible((2, math.inf, 4), 2, 4))
        self.assertFalse(plausible((2, 1.4, 4), 0, 4))

    def test_source_helper_has_finite_checks_bounded_real_metre_limits(self):
        s = WORLD.split("internal static bool HasPlausibleAuthoredSilhouette(", 1)[1].split(
            "// FBX exporters disagree", 1)[0]
        for part in (
            "float.IsNaN(size.sqrMagnitude)",
            "float.IsInfinity(size.sqrMagnitude)",
            "targetWidth * .74f",
            "targetWidth * 1.08f",
            "targetLength * .78f",
            "targetLength * 1.08f",
            "size.y >= .82f && size.y <= 2.58f",
            "size.x >= size.z * .28f",
            "size.x <= size.z * .72f",
        ):
            self.assertIn(part, s)
        for forbidden in ("new Material(", "Physics.Raycast", "Instantiate(", "Debug.Log"):
            self.assertNotIn(forbidden, s)

    def test_native_debug_metrics_logged_once_after_spawn_not_every_frame(self):
        build = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn('AARIS_CAR_RENDER_METRES id=', build)
        self.assertIn('" width=" + bounds.size.x.ToString("F2")', build)
        self.assertIn('" height=" + bounds.size.y.ToString("F2")', build)
        self.assertIn('" length=" + bounds.size.z.ToString("F2")', build)
        self.assertIn("if (Debug.isDebugBuild)", build)
        self.assertEqual(WORLD.count("AARIS_CAR_RENDER_METRES"), 1)
        driving = WORLD.split("private void Update()", 1)[1]
        self.assertNotIn("AARIS_CAR_RENDER_METRES", driving)


if __name__ == "__main__":
    unittest.main()
