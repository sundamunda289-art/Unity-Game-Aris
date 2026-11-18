#!/usr/bin/env python3
"""Offline real-metre vehicle scale/paint/tyre positioning contracts.

These are geometric and source tests, NOT native Unity or Android screenshots.
"""
import json
import math
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()
CATALOG = json.loads((ROOT / "Assets/Resources/AarisVehicles/vehicle_catalog.json").read_text())

DIMENSIONS = {
    "Sedan": (1.96, 4.50), "SUV": (2.10, 4.80),
    "Sports": (1.99, 4.46), "GT": (2.08, 4.48),
    "Hatchback": (1.81, 3.90), "Van": (2.05, 5.08),
    "Utility": (2.10, 5.42), "Buggy": (1.90, 3.45),
}


class RealMetreVehicleFit(unittest.TestCase):
    def test_all_eight_vehicle_archetypes_fit_road_without_flattening(self):
        self.assertEqual({v["id"] for v in CATALOG["vehicles"]}, set(DIMENSIONS))
        for v in CATALOG["vehicles"]:
            width, length = DIMENSIONS[v["id"]]
            self.assertAlmostEqual(v["referenceWidthMetres"], width)
            self.assertAlmostEqual(v["referenceLengthMetres"], length)
            self.assertGreater(width, 1.60)
            self.assertGreater(length, 3.35)
            # Per-class road-clearance overlap follows these proportions.
            self.assertLess(width, 2.25)
            self.assertLess(length, 5.50)
            # The saved playable avatar measures 1.78 metres tall.
            self.assertGreater(width / 1.78, .90)
            self.assertLess(width / 1.78, 1.18)
            self.assertLess(width / length, .60)
        self.assertLess(DIMENSIONS["Hatchback"][1], DIMENSIONS["Sedan"][1])
        self.assertGreater(DIMENSIONS["Van"][1], DIMENSIONS["Sedan"][1])
        self.assertGreater(DIMENSIONS["SUV"][0], DIMENSIONS["Hatchback"][0])

    def test_uniform_scale_preserves_body_and_axle_aspect(self):
        fit = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("referenceWidthMetres", WORLD)
        self.assertIn("referenceLengthMetres", WORLD)
        self.assertIn("Mathf.Clamp(widthMetres, 1.65f, 2.15f)", fit)
        self.assertIn("Mathf.Clamp(lengthMetres, 3.35f, 5.50f)", fit)
        self.assertIn("Mathf.Min(widthMetres / bounds.size.x,", fit)
        self.assertIn("lengthMetres / bounds.size.z)", fit)
        self.assertIn("mesh.transform.localScale *= Mathf.Clamp(scale, .01f, 50f)", fit)
        self.assertNotIn("mesh.transform.localScale = new Vector3(", fit)
        for name, body_w, body_l in (
            ("Hatchback", 2.00, 4.10), ("SUV", 2.15, 5.30),
            ("GT", 2.09, 4.63), ("Van", 2.10, 5.10),
            ("Buggy", 1.81, 3.25)):
            desired_w, desired_l = DIMENSIONS[name]
            factor = min(desired_w / body_w, desired_l / body_l)
            final_w, final_l = body_w * factor, body_l * factor
            self.assertLessEqual(final_w, desired_w + 1e-8)
            self.assertLessEqual(final_l, desired_l + 1e-8)
            self.assertAlmostEqual(final_w / final_l, body_w / body_l, places=10)
            self.assertGreater(final_w, 1.60)

    def test_spawn_ground_tyre_alignment_only_for_authored_wheels(self):
        helper = WORLD.split("private static bool AlignEmbeddedTyresToRoad(", 1)[1].split(
            "private static bool HasEmbeddedWheelSet(", 1)[0]
        for token in (
            '"wheel_fl"', '"wheel_fr"', '"wheel_rl"', '"wheel_rr"',
            "TryGetVisualBoundsLocal(", "tyreBounds.min.y",
            "maximumBottom - minimumBottom > .22f",
            "const float TyreBottomInChassis = -.71f + .10f",
            "model.localPosition += Vector3.up * offset",
        ):
            self.assertIn(token, helper)
        self.assertNotIn("Physics.Raycast", helper)
        self.assertNotIn("new Material(", helper)
        build = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("if (record.type.useEmbeddedAuthoredWheels)\n"
                      "                AlignEmbeddedTyresToRoad(", build)
        self.assertLess(build.index("AlignEmbeddedTyresToRoad("),
                        build.index("motor.FitShellAndWheelbase(bounds)"))
        self.assertIn("motor.ConfigureExhaust(exhaustMaterial, bounds);", build)
        self.assertIn("Bounds lampBounds = bounds;", build)
        self.assertIn("motor.FitShellAndWheelbase(bounds);", build)
        self.assertNotIn("new Bounds(new Vector3(0f, .48f, 0f)", build)
        for min_bottom in (-.58, -.15, .02, .25):
            offset = (-.71 + .10) - min_bottom
            self.assertAlmostEqual(min_bottom + offset, -.61)
        self.assertIn("Mathf.Clamp(visualBounds.min.y + .10f, -.35f, .40f)", MOTOR)

    def test_subtle_premium_paint_and_closed_opaque_glass(self):
        paint = WORLD.split("private static readonly Color[] BodyPaintColours =", 1)[1].split(
            "private static int StablePaintVariant(", 1)[0]
        values = re.findall(r"new Color\(([^)]*)\)", paint)
        self.assertEqual(len(values), 8)
        for raw in values:
            rgb = [float(x.rstrip("f")) for x in raw.split(",")[:3]]
            self.assertTrue(all(.10 <= c <= .90 for c in rgb))
            self.assertLess(max(rgb) - min(rgb), .70)
        tint = WORLD.split("private static void PaintVerifiedBodySlots(", 1)[1].split(
            "private void FallBackToVerifiedModel(", 1)[0]
        self.assertIn('case "GT":', tint)
        self.assertIn("paintClearCoat = .41f", tint)
        self.assertIn("if (separatePaint || civilianPaint)", tint)
        self.assertIn("renderer.SetPropertyBlock(block, slot)", tint)
        material = WORLD.split("private Material AuthoredUrpMaterial(", 1)[1].split(
            "private void ApplyAuthoredMaterials(", 1)[0]
        self.assertIn('sourceLabel.Contains("_glass")', material)
        self.assertIn('sourceLabel.Contains("window")', material)
        self.assertIn('polished.SetFloat("_Smoothness", .83f)', material)
        self.assertIn("authoredMaterialCache[source] = polished", material)
        self.assertNotIn("renderer.material", material)

    def test_vendor_fbx_orientation_is_detected_before_scale(self):
        build = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("bounds.size.y > 2.75f", build)
        self.assertIn("bounds.size.x > .8f && bounds.size.z > .7f", build)
        self.assertIn("Quaternion.Euler(-90f, 0f, 0f)", build)
        self.assertIn('"source axis correction"', build)
        self.assertLess(build.index("Quaternion.Euler(-90f, 0f, 0f)"),
                        build.index("FaceEmbeddedVehicleForward("))
        self.assertLess(build.index("FaceEmbeddedVehicleForward("),
                        build.index("float scale = Mathf.Min("))
        self.assertIn("Quaternion.Euler(0f, 90f, 0f) * mesh.transform.localRotation", build)
        # A Y-up, Z-forward prefab is not rotated by this geometry test.
        shapes = (
            ((1.90, 4.9, 1.4), True),  # raw vendor car length on Y
            ((1.90, 1.4, 4.9), False),  # Unity importer already Y-up
            ((2.1, 1.8, 5.4), False),
            ((1.0, 1.6, 2.0), False),
        )
        for (x, y, z), expected in shapes:
            detected = y > 2.75 and y > max(x, z) * 1.30 and x > .8 and z > .7
            self.assertEqual(detected, expected)

    def test_map_clearance_tracks_long_pickups_not_only_sedans(self):
        resolver = WORLD.split("private bool TryResolve(", 1)[1].split(
            "private bool TryResolvePlanned(", 1)[0]
        for fragment in (
            "record.type.referenceWidthMetres",
            "record.type.referenceLengthMetres",
            "Mathf.Max(1.42f, lengthMetres * .305f)",
            "Mathf.Max(.79f, widthMetres * .405f)",
            "Mathf.Max(1.14f, widthMetres * .55f)",
            "Mathf.Max(2.35f, lengthMetres * .55f)",
            "tallBody ? .94f : .64f",
            "Physics.OverlapBoxNonAlloc",
        ):
            self.assertIn(fragment, resolver)
        self.assertIn("Mathf.Clamp(size.z * .94f, 2.35f, 5.20f)", MOTOR)
        self.assertIn("Mathf.Clamp(size.z * .322f, .92f, 1.72f)", MOTOR)
        for width, length in DIMENSIONS.values():
            half_w = max(1.14, width * .55)
            half_l = max(2.35, length * .55)
            self.assertGreaterEqual(half_w * 2, width)
            self.assertGreaterEqual(half_l * 2, length)
            self.assertLess(half_w * 2, 2.55)
            self.assertLess(half_l * 2, 6.0)
        self.assertGreater(DIMENSIONS["Utility"][1], DIMENSIONS["SUV"][1])


if __name__ == "__main__":
    unittest.main(verbosity=2)
