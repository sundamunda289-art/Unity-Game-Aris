#!/usr/bin/env python3
"""Premium vehicle paint must be distinguishable and not recolour tyres/lenses."""
import itertools
import math
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()
EDITOR = (ROOT / "Assets/AarisWorld/Editor/AarisPremiumGtPaintQA.cs").read_text()

def colors():
    palette = WORLD.split("private static readonly Color[] BodyPaintColours =",1)[1].split("};",1)[0]
    return [tuple(float(x) for x in match) for match in re.findall(
        r'new Color\(([\d.]+)f, ([\d.]+)f, ([\d.]+)f, 1f\)', palette)]

class PremiumPaintSeparation(unittest.TestCase):
    def test_eight_production_colors_have_clear_separation(self):
        shades = colors()
        self.assertEqual(len(shades),8)
        for shade in shades:
            self.assertTrue(all(.09 <= component <= .90 for component in shade))
            self.assertTrue(all(math.isfinite(x) for x in shade))
            self.assertGreater(max(shade)-min(shade),0.005)
        for i,j in itertools.combinations(range(8),2):
            distance = math.dist(shades[i],shades[j])
            self.assertGreaterEqual(distance,.235,
                f"Premium paint #{i} vs #{j} too close ({distance:.3f})")

    def test_spawn_variant_uses_stable_cross_session_hash(self):
        selector = WORLD.split("private static int StablePaintVariant(",1)[1].split(
            "internal static Color SelectPaintTint(",1)[0]
        self.assertIn("2166136261u",selector)
        self.assertIn("16777619u",selector)
        self.assertIn("pointId ?? string.Empty",selector)
        self.assertIn("vehicleId ?? string.Empty",selector)
        self.assertIn("BodyPaintColours.Length",selector)
        self.assertNotIn("GetHashCode()",selector)
        self.assertNotIn("UnityEngine.Random",selector)

    def test_original_urp_metal_and_glass_remain_separate(self):
        method = WORLD.split("private static void PaintVerifiedBodySlots(",1)[1].split(
            "private void FallBackToVerifiedModel(",1)[0]
        self.assertIn('label.IndexOf("AarisPaint"',method)
        self.assertIn('label.IndexOf("body_pbr"',method)
        self.assertIn('label.IndexOf("AarisCivilianSurface"',method)
        self.assertIn("if (!separatePaint && !bodyAtlas && !civilianPaint) continue;",method)
        self.assertIn("renderer.SetPropertyBlock(block, slot);",method)
        self.assertNotIn("new Material(",method)
        self.assertNotIn("renderer.material",method)

    def test_native_visual_qa_invokes_actual_runtime_paint(self):
        self.assertIn('GetMethod(\n                "PaintVerifiedBodySlots", StaticPrivate)',EDITOR)
        self.assertIn('"StablePaintVariant", StaticPrivate',EDITOR)
        self.assertIn("paint.Invoke(null, new[] { (object)car, record });",EDITOR)
        self.assertIn("r.GetPropertyBlock(block, i);",EDITOR)
        self.assertIn("for (int i = 0; i < 8; i++)",EDITOR)
        self.assertIn('AARIS_GT_EIGHT_REAL_PAINT_QA_PASS',EDITOR)
        self.assertNotIn("AssetDatabase.SaveAssets()",EDITOR)
        self.assertNotIn("PrefabUtility.SaveAsPrefabAsset",EDITOR)

if __name__ == "__main__":
    unittest.main()
