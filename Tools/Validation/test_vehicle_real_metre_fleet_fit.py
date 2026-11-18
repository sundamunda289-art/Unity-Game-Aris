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
    "Sedan": (1.84, 4.42), "SUV": (1.96, 4.50),
    "Sports": (1.91, 4.40), "GT": (1.92, 4.42),
    "Hatchback": (1.74, 3.95), "Van": (1.97, 4.57),
    "Utility": (1.96, 4.57), "Buggy": (1.80, 3.72),
}


class RealMetreVehicleFit(unittest.TestCase):
    def test_all_eight_vehicle_archetypes_fit_road_without_flattening(self):
        self.assertEqual({v["id"] for v in CATALOG["vehicles"]}, set(DIMENSIONS))
        for v in CATALOG["vehicles"]:
            width, length = DIMENSIONS[v["id"]]
            self.assertAlmostEqual(v["referenceWidthMetres"], width)
            self.assertAlmostEqual(v["referenceLengthMetres"], length)
            self.assertGreater(width, 1.60)
            self.assertGreater(length, 3.50)
            # Existing occupied-patch protection is 2.28 m x 4.70 m.
            self.assertLess(width, 2.28)
            self.assertLess(length, 4.70)
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
        self.assertIn("Mathf.Clamp(widthMetres, 1.65f, 2.08f)", fit)
        self.assertIn("Mathf.Clamp(lengthMetres, 3.50f, 4.60f)", fit)
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
