#!/usr/bin/env python3
"""Android 4GB loot-streaming model-build pacing (static/algorithmic checks).

The test verifies source and bounded attempts; it cannot demonstrate an
actual Unity Player FPS gain or prove the cause of an Android OS freeze.
"""
import random
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs").read_text(
    encoding="utf-8"
)
MOBILE = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text(
    encoding="utf-8"
)


def budget(known_budget_phone, smooth_delta_seconds, target_fps=45):
    # Same 8% jitter allowance as vehicle streaming and the shared frame gate.
    return 1 if known_budget_phone and (
        smooth_delta_seconds > (1 / target_fps) * 1.08
    ) else 2


class AndroidLootFramePressureTests(unittest.TestCase):
    def test_baseline_max_two_preserved_and_budget_only_on_android(self):
        self.assertIn("private const int MaximumBuildAttemptsPerFrame = 2;", SRC)
        self.assertIn("private const int MaximumActiveLoot = 40;", SRC)
        self.assertIn("private const int MaximumCandidateProbesPerFrame = 6;", SRC)
        self.assertIn("private const int StreamFramesPerRefresh = 3;", SRC)
        self.assertIn("int modelBuildBudget = MaximumBuildAttemptsPerFrame;", SRC)
        self.assertIn("if (budgetAndroidLootStreaming &&", SRC)
        self.assertIn("Time.smoothDeltaTime >", SRC)
        self.assertIn(
            "AarisMobileStreamingFrameGate.CurrentPacedFrameBudgetSeconds() * 1.08f",
            SRC)
        self.assertNotIn("Time.smoothDeltaTime > (1f / 45f)", SRC)
        self.assertIn("modelBuildBudget = 1;", SRC)
        init = SRC.split("private void Initialize()", 1)[1].split(
            "private IEnumerator CompleteAndroidInitialization", 1
        )[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", init)
        self.assertIn("ramMB > 0 && ramMB <= 4096", init)
        self.assertIn("AarisMobileRuntime.UseConstrainedGpuStreamingBudget()", init)
        self.assertIn("internal static bool UseConstrainedGpuStreamingBudget()", MOBILE)

    def test_classification_never_runs_from_frame_hotpath(self):
        stream = SRC.split("private bool StreamNearby(", 1)[1].split(
            "private void EnsureStarterLoot(", 1
        )[0]
        self.assertNotIn("SystemInfo.systemMemorySize", stream)
        self.assertNotIn("UseConstrainedGpuStreamingBudget()", stream)
        self.assertNotIn("SystemInfo.graphicsDeviceName", stream)
        self.assertNotIn("new List<", stream)
        self.assertNotIn("GC.Collect(", stream)
        self.assertNotIn("Resources.UnloadUnusedAssets()", stream)
        self.assertIn("if (buildAttempts >= modelBuildBudget) return true;", stream)
        self.assertIn("if (buildAttempts >= modelBuildBudget ||", stream)
        self.assertIn("return buildAttempts >= modelBuildBudget;", stream)

    def test_only_paces_model_creation_not_ground_safety_or_pickup(self):
        stream = SRC.split("private bool StreamNearby(", 1)[1].split(
            "private void EnsureStarterLoot(", 1
        )[0]
        self.assertIn("TryResolveNearCandidate(c, i, terrains,", stream)
        self.assertIn("if (IsCurrentlyActive(i)) continue;", stream)
        self.assertIn("if (active.Count >= streamLimit) return false;", stream)
        self.assertIn("root.AddComponent<AarisWeaponPickup>();", stream)
        self.assertIn("buildAttempts++", stream)
        self.assertIn("record.nextVisualAttemptAt = now + RetryDelaySeconds;", stream)
        self.assertIn("nextCandidateProbeAt[i] = now + RetryDelaySeconds;", stream)
        update = SRC.split("private void Update()", 1)[1].split(
            "private bool RefreshPickups(", 1
        )[0]
        self.assertIn("RefreshPickups(playerPosition, now)", update)
        self.assertIn("EnsureStarterLoot(playerPosition);", update)
        self.assertIn("if (!starterLootSettled && now >= nextStarterRetryAt)", update)

    def test_simulated_streaming_does_not_starve_under_sustained_pressure(self):
        rng = random.Random(20261119)
        for _ in range(160):
            count = rng.randrange(1, 120)
            # Model work is never zero on a pressured frame, regardless
            # of how many pending authored/dropped pickups are waiting.
            for slow in (False, True):
                frames = 0
                left = count
                while left > 0:
                    capacity = budget(True, 0.033 if slow else 0.016)
                    self.assertGreaterEqual(capacity, 1)
                    self.assertLessEqual(capacity, 2)
                    left -= min(left, capacity)
                    frames += 1
                self.assertLessEqual(frames, count)
        self.assertEqual(budget(False, 0.050), 2)
        self.assertEqual(budget(True, 0.050), 1)
        self.assertEqual(budget(True, 1 / 45), 2)
        self.assertEqual(budget(True, 0.012), 2)

    def test_30_fps_healthy_is_not_always_throttled_by_45_fps_constant(self):
        for target in (30, 45, 60, 90):
            healthy = 1.0 / target
            self.assertEqual(budget(True, healthy, target), 2)
            self.assertEqual(budget(True, healthy * 1.06, target), 2)
            self.assertEqual(budget(True, healthy * 1.16, target), 1)
            self.assertEqual(budget(False, healthy * 2.5, target), 2)
        self.assertEqual(budget(True, 0.034, 30), 2)
        self.assertEqual(budget(True, 0.034, 60), 1)
        self.assertEqual(budget(True, 0.051, 30), 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)
