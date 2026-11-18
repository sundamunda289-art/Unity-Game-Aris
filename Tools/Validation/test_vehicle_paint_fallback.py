#!/usr/bin/env python3
"""Static regression guards for deterministic authored vehicle paint and fallback.
Native Unity/Android renders and WheelCollider play tests are separate.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()


class VehiclePaintAndFallback(unittest.TestCase):
    def test_paint_is_stable_and_instance_scoped(self):
        self.assertIn("private static int StablePaintVariant(string pointId, string vehicleId)", WORLD)
        self.assertIn("uint hash = 2166136261u;", WORLD)
        self.assertIn("16777619u", WORLD)
        self.assertNotIn("GetHashCode()", WORLD.split("private static int StablePaintVariant(", 1)[1].split("private static void PaintVerifiedBodySlots", 1)[0].replace("String.GetHashCode is not a persistence key.", ""))
        paint = WORLD.split("private static void PaintVerifiedBodySlots(", 1)[1].split("private void FallBackToVerifiedModel(", 1)[0]
        for term in ('new MaterialPropertyBlock()', 'renderer.GetPropertyBlock(block, slot)', 'renderer.SetPropertyBlock(block, slot)',
                     'label.IndexOf("AarisPaint"', 'label.IndexOf("body_pbr"', 'if (!separatePaint && !bodyAtlas && !civilianPaint) continue;'):
            self.assertIn(term, paint)
        self.assertIn("Color.Lerp(Color.white, paint, .30f)", WORLD)
        self.assertIn('label.IndexOf("AarisCivilianSurface"', paint)
        self.assertIn("Color.Lerp(Color.white, paint, .45f)", WORLD)
        self.assertNotIn("renderer.material", paint)
        self.assertNotIn("new Material(", paint)

    def test_fallback_only_for_failed_imported_art(self):
        self.assertIn("safeBodyPrefabs[i] = prefabs[i];", WORLD)
        self.assertIn("safeWheelPrefabs[i] = wheelPrefabs[i];", WORLD)
        fallback = WORLD.split("private void FallBackToVerifiedModel(", 1)[1].split("private bool Build(", 1)[0]
        self.assertIn("ReferenceEquals(prefabs[index], safeBodyPrefabs[index])", fallback)
        self.assertIn("prefabs[index] = safeBodyPrefabs[index];", fallback)
        self.assertIn("wheelPrefabs[index] = safeWheelPrefabs[index];", fallback)
        self.assertIn("useEmbeddedAuthoredWheels = false;", fallback)
        build = WORLD.split("private bool Build(", 1)[1].split("private void Update()", 1)[0]
        for failure in ("renderer check", "visual bounds", "model orientation", "model scale", "wheel attachment"):
            self.assertIn(f'FallBackToVerifiedModel(typeIndex, "{failure}")', build)
        self.assertLess(build.index("PaintVerifiedBodySlots(mesh, record)"), build.index("TryGetVisualBoundsLocal(root.transform, out Bounds bounds)"))

    def test_urp_body_paint_assets_exist(self):
        assets = list((ROOT / "Assets/Resources/AarisVehicles/Authored").glob("PBR_*/*body_pbr.mat"))
        self.assertEqual(len(assets), 5)
        for a in assets:
            self.assertIn("m_EnableInstancingVariants: 1", a.read_text())


    def test_premium_palette_and_panel_only_clearcoat(self):
        palette = WORLD.split("private static readonly Color[] BodyPaintColours =", 1)[1].split("private static int StablePaintVariant(", 1)[0]
        self.assertEqual(palette.count("new Color("), 8)
        for shade in ("ceramic pearl", "satin titanium", "graphite blue steel", "deep burgundy", "navy sapphire", "muted olive", "antique champagne", "dark petrol teal"):
            self.assertIn(shade, palette)
        paint = WORLD.split("private static void PaintVerifiedBodySlots(", 1)[1].split("private void FallBackToVerifiedModel(", 1)[0]
        self.assertIn("if (separatePaint || civilianPaint)", paint)
        self.assertIn('block.SetFloat("_Smoothness"', paint)
        self.assertIn('block.SetFloat("_Metallic"', paint)
        self.assertLess(paint.index("if (!separatePaint && !bodyAtlas && !civilianPaint) continue;"), paint.index("if (separatePaint || civilianPaint)"))
        self.assertNotIn("renderer.material", paint)
        self.assertNotIn("new Material(", paint)


if __name__ == "__main__":
    unittest.main()
