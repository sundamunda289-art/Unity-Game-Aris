#!/usr/bin/env python3
"""Regression guard for reversible low-end Android terrain draw-distance caps.

Not an Android profiler or Unity Play Mode test.
"""
import math
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RUNTIME = ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs"
SCENE = ROOT / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"

def scoped_tier(angle_ratio, current_tier):
    if angle_ratio <= (.49 if current_tier == 2 else .45):
        return 2
    return int(angle_ratio <= (.67 if current_tier else .64))

class TerrainDrawRangeBudget(unittest.TestCase):
    def test_source_terrain_is_untouched(self):
        data = SCENE.read_text()
        self.assertEqual(re.findall(r"(?m)^  m_TreeDistance: (\d+)$", data), ["850"] * 4)

    def test_android_only_constrained_gpu_caps_draw_distance(self):
        code = RUNTIME.read_text()
        setup = code.split("private void ConfigureLowMemoryVegetation()", 1)[1]
        setup = setup.split("private static float EffectiveAndroidTargetFps(", 1)[0]
        self.assertIn("originalTreeDistances[i] = tile.treeDistance;", setup)
        self.assertIn("? 450f : 600f;", setup)
        self.assertIn("Mathf.Min(originalTreeDistances[i],", setup)
        self.assertIn("tile.treeDistance = appliedTreeDistances[i];", setup)
        self.assertIn("if (constrainedGraphics)", code)
        self.assertIn("ConfigureLowMemoryVegetation();", code)

    def test_scoped_range_respects_screen_sector_even_on_hysteresis_exit(self):
        code = RUNTIME.read_text()
        self.assertIn("Mathf.Atan(currentTangent / zoom)", code)
        self.assertIn("SystemInfo.systemMemorySize > 3072 && count > 0;", code)
        self.assertIn("!AarisAndroidThermalGovernor.SuspendSpeculativePrefetch", code)
        self.assertIn("!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()) return;", code)
        self.assertIn("ScopedVegetationDistance(600f, originalTreeDistances[i], desired)", code)
        self.assertIn("ScopedVegetationDistance(80f, originalDetailDistances[i], desired)", code)
        for previous in (0, 1, 2):
            for percent in range(1, 101):
                fraction = percent / 100
                multiplier = (1, 1.2, 850 / 600)[scoped_tier(fraction, previous)]
                self.assertLessEqual(fraction * multiplier ** 2, 1)
        self.assertEqual(scoped_tier(.47, 1), 1)
        self.assertEqual(scoped_tier(.47, 2), 2)
        horizontal_tangent = math.tan(math.radians(60 / 2)) * (16 / 9)
        for zoom, tier in ((1, 0), (.5, 1), (.25, 2)):
            ratio = math.atan(horizontal_tangent * zoom) / math.atan(horizontal_tangent)
            self.assertEqual(scoped_tier(ratio, 0), tier)

    def test_cleanup_restores_only_unchanged_instance_state(self):
        code = RUNTIME.read_text()
        end = code.split("private void OnDestroy()", 1)[1]
        self.assertIn("Mathf.Approximately(tile.treeDistance, appliedTreeDistances[i])", end)
        self.assertIn("tile.treeDistance = originalTreeDistances[i];", end)


if __name__ == "__main__":
    unittest.main(verbosity=2)
