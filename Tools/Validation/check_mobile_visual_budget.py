"""Static regression checks for low-RAM Android visual budgets and cypress silhouette.

These inspect serialized text only. They do not certify a Unity import or device FPS.
"""
from __future__ import annotations

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def source(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


class MobileVisualBudgetChecks(unittest.TestCase):
    def test_cypress_prefab_scale_and_lod(self) -> None:
        prefab = source(
            "Assets/TerrainDemoScene_URP/Prefabs/Trees/Cypress/"
            "Cypress_Forest_Desktop.prefab"
        )
        self.assertEqual(
            prefab.count("m_LocalScale: {x: 1.68, y: 1.42, z: 1.68}"), 1
        )
        self.assertIn("m_Name: Cypress_Forest_Desktop", prefab)
        self.assertIn("BoxCollider:", prefab)
        self.assertIn("LODGroup:", prefab)
        thresholds = [
            float(value)
            for value in re.findall(r"screenRelativeHeight:\s*([\d.]+)", prefab)
        ]
        self.assertEqual(len(thresholds), 4)
        self.assertEqual(thresholds, sorted(thresholds, reverse=True))
        self.assertGreater(1.68 / 1.42, 1.15)

    def test_budget_is_android_only_and_preserves_distant_trees(self) -> None:
        runtime = source("Assets/AarisWorld/Scripts/AarisMobileRuntime.cs")
        self.assertIn("private bool adaptiveAndroidVisualBudget = true;", runtime)
        self.assertIn("ConfigureAndroidVisualBudget();", runtime)
        self.assertIn("ApplyMemoryPressureBudget();", runtime)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", runtime)
        self.assertIn("SystemInfo.systemMemorySize", runtime)
        self.assertIn("QualitySettings.streamingMipmapsMemoryBudget", runtime)
        self.assertIn("terrain.treeBillboardDistance", runtime)
        self.assertIn("terrain.treeMaximumFullLODCount", runtime)
        self.assertIn("terrain.detailObjectDistance", runtime)
        self.assertNotRegex(runtime, r"\btreeDistance\s*=")
        self.assertIn('mobileVisualProfile = twoGB ? "2gb-balanced" : "3gb-balanced"', runtime)
        self.assertIn('mobileVisualProfile = "memory-pressure"', runtime)
        self.assertIn("textureStreamingBudgetMB", runtime)
        self.assertIn("shadowDistanceM", runtime)
        self.assertIn("texture.Apply(false, true)", runtime)
        self.assertNotIn("GC.Collect", runtime)
        quality = source("ProjectSettings/QualitySettings.asset")
        self.assertRegex(quality, r"(?m)^\s*streamingMipmapsActive: 1$")

    def test_physics_refresh_never_syncs_on_unchanged_poll(self) -> None:
        runtime = source("Assets/AarisWorld/Scripts/AarisNearbyColliders.cs")
        self.assertIn("RefreshInternal(false)", runtime)
        self.assertIn("RefreshInternal(true)", runtime)
        self.assertIn("bool colliderStateChanged = false;", runtime)
        self.assertIn(
            "if (forceSync || colliderStateChanged) Physics.SyncTransforms();",
            runtime,
        )
        self.assertEqual(runtime.count("Physics.SyncTransforms();"), 1)
        self.assertIn("if (wanted[i] != query", runtime)


if __name__ == "__main__":
    unittest.main(verbosity=2)
