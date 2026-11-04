"""Source-only guards for sustained-pressure Terrain tree mesh LOD budgets.

This checks code and cap arithmetic, not Unity import or measured Android FPS.
"""
from pathlib import Path
import math
import unittest

ROOT = Path(__file__).resolve().parents[3]
SOURCE = ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs"


class AdaptiveTreeMeshPressureContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.src = SOURCE.read_text(encoding="utf-8")

    def test_only_low_ram_android_profile_is_affected(self):
        s = self.src
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR\n            ApplyMobileMemoryProfile();", s)
        self.assertIn("if (systemRamMb <= 0 || systemRamMb > 4096) return;", s)
        self.assertIn("profileTreeBillboardDistance = constrained ? 100f : 140f;", s)
        self.assertIn("profileMaxFullLodTrees = constrained ? 28 : 44;", s)
        self.assertIn("if (!mobileProfileApplied) return;", s)

    def test_pressure_requires_p95_and_is_reversible(self):
        s = self.src
        for fragment in (
            "frameCount < frameTimes.Length || Time.unscaledTime < 15f",
            "pressureSamples >= 2 && performanceLevel < 2",
            "nextPerformanceChangeAt = Time.unscaledTime + 12f;",
            "treeMeshDistanceFactor = performanceLevel == 0 ? 1f : performanceLevel == 1 ? 0.92f : 0.82f;",
            "treeMeshCountFactor = performanceLevel == 0 ? 1f : performanceLevel == 1 ? 0.86f : 0.72f;",
            "profileTreeBillboardDistance * treeMeshDistanceFactor",
            "Mathf.Max(1, Mathf.FloorToInt(profileMaxFullLodTrees * treeMeshCountFactor * lowMemoryTreeMeshMultiplier))",
            "terrain.treeBillboardDistance = originalTreeBillboardDistances[i];",
            "terrain.treeMaximumFullLODCount = originalFullLodTreeCounts[i];",
        ):
            with self.subTest(fragment=fragment):
                self.assertIn(fragment, s)


    def test_low_memory_mesh_cap_survives_quality_recovery(self):
        s = self.src
        for fragment in (
            "lowMemoryTreeMeshMultiplier = 1f;",
            "lowMemoryTreeMeshMultiplier = Mathf.Min(lowMemoryTreeMeshMultiplier, 0.80f);",
            "profileTreeBillboardDistance * treeMeshDistanceFactor * lowMemoryTreeMeshMultiplier",
            "profileMaxFullLodTrees * treeMeshCountFactor * lowMemoryTreeMeshMultiplier",
            "s.lowMemoryTreeMeshMultiplier = lowMemoryTreeMeshMultiplier;",
        ):
            with self.subTest(fragment=fragment):
                self.assertIn(fragment, s)
        # After a low-memory warning, even at pressure level 0, the
        # tree mesh caps remain lower than the original RAM-class baseline.
        for baseline_distance, baseline_count in ((100, 28), (140, 44)):
            self.assertLess(baseline_distance * .80, baseline_distance)
            self.assertLess(math.floor(baseline_count * .80), baseline_count)

    def test_player_does_not_override_runtime_startup_fps_cap(self):
        player = (ROOT / "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs").read_text(encoding="utf-8")
        self.assertIn("Application.targetFrameRate = targetFrameRate;", self.src)
        self.assertNotIn("Application.targetFrameRate = 60;", player)
        self.assertIn("Application.targetFrameRate == 60 ? 30 : 60", player)

    def test_distant_visibility_and_readout_are_retained(self):
        s = self.src
        self.assertNotIn("terrain.treeDistance =", s)
        self.assertNotIn("farClipPlane =", s)
        self.assertIn("s.effectiveTreeBillboardDistanceM = terrain.treeBillboardDistance;", s)
        self.assertIn("s.effectiveMaxFullLodTrees = terrain.treeMaximumFullLODCount;", s)
        self.assertIn("Mathf.Min(originalTreeBillboardDistances[i],", s)
        self.assertIn("Mathf.Min(originalFullLodTreeCounts[i],", s)

    def test_expected_max_caps_monotonic(self):
        for base_distance, base_mesh_count in ((100, 28), (140, 44)):
            distances = [base_distance * x for x in (1, 0.92, 0.82)]
            counts = [math.floor(base_mesh_count * x) for x in (1, 0.86, 0.72)]
            self.assertTrue(all(distances[i] >= distances[i + 1] for i in range(2)))
            self.assertTrue(all(counts[i] >= counts[i + 1] >= 1 for i in range(2)))


if __name__ == "__main__":
    unittest.main()
