#!/usr/bin/env python3
"""Regression contracts for 4GB Android loot-tree time slicing.

Structural tests only; target-device FPS and Unity compilation remain separate.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs").read_text()


class TimeSlicedNativeLoot(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.begin = SRC.split("private void Initialize()", 1)[1].split(
            "private void CompleteInitialization(", 1
        )[0]
        cls.finish = SRC.split("private void CompleteInitialization(", 1)[1].split(
            "private static int CandidateCell(", 1
        )[0]
        cls.tree = SRC.split("private IEnumerator AppendNativeTreeCoverBatched(", 1)[1].split(
            "private static bool WithinMap(", 1
        )[0]

    def test_android_yields_before_publishing_planned_loot(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", self.begin)
        self.assertIn("StartCoroutine(CompleteAndroidInitialization(allCandidates));", self.begin)
        self.assertIn("yield return AppendNativeTreeCoverBatched(allCandidates, true);", self.begin)
        self.assertIn("CompleteInitialization(allCandidates);", self.begin)
        self.assertIn("AppendNativeTreeCover(allCandidates);", self.begin)
        # The Editor path correctly completes synchronously *before* the
        # Android coroutine declaration. Assert ordering within the coroutine,
        # not against the earlier desktop method invocation.
        android = self.begin.split(
            "private IEnumerator CompleteAndroidInitialization(", 1
        )[1]
        self.assertLess(
            android.index("yield return AppendNativeTreeCoverBatched(allCandidates, true);"),
            android.index("CompleteInitialization(allCandidates);")
        )
        self.assertIn("candidates = allCandidates.ToArray();", self.finish)
        self.assertNotIn("candidates = allCandidates.ToArray();", self.begin)

    def test_original_rng_and_tree_distribution_kept(self):
        self.assertIn("AarisWeaponRandom.Next(ref rng)", self.tree)
        self.assertIn("AarisWeaponRandom.Next01(ref rng)", self.tree)
        self.assertIn("data.GetTreeInstance(idx)", self.tree)
        self.assertIn("accepted < 48 && attempts++ < 240", self.tree)
        self.assertIn("dx * dx + dz * dz < 32f * 32f", self.tree)
        self.assertIn('origin = "native Terrain tree", slot = idx', self.tree)
        self.assertIn("var treePoints = new List<Vector2>(192);", self.tree)
        self.assertIn("int examinedThisFrame = 0;", self.tree)
        self.assertIn("if (timeSlice && examinedThisFrame++ >= 32)", self.tree)
        self.assertIn("yield return null;", self.tree)
        self.assertIn("if (timeSlice) yield return null;", self.tree)

    def test_desktop_direct_synchronous_path_preserved(self):
        sync = SRC.split("private void AppendNativeTreeCover(", 1)[1].split(
            "private IEnumerator AppendNativeTreeCoverBatched(", 1
        )[0]
        self.assertIn("AppendNativeTreeCoverBatched(result, false)", sync)
        self.assertIn("while (iterator.MoveNext())", sync)
        self.assertIn("AppendNativeTreeCover(allCandidates);", self.begin)
        self.assertIn("CompleteInitialization(allCandidates);", self.begin)

    def test_no_half_initialized_streaming_or_visual_downgrade(self):
        update = SRC.split("private void Update()", 1)[1]
        self.assertIn("if (player == null || inventory == null || candidates == null) return;", update)
        self.assertIn("private const int MaximumActiveLoot = 40;", SRC)
        self.assertIn("private const int MaximumBuildAttemptsPerFrame = 2;", SRC)
        self.assertIn("private const int StreamFramesPerRefresh = 3;", SRC)
        self.assertIn("IndexPlannedCandidates();", self.finish)
        self.assertIn("EnsureStarterLoot(playerPosition);", update)
        self.assertNotIn("Destroy(", self.tree)
        self.assertNotIn("Instantiate(", self.tree)


if __name__ == "__main__":
    unittest.main()
