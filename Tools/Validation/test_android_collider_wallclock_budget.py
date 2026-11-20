#!/usr/bin/env python3
"""Android collider startup frame-time pacing: deterministic source contracts.

Native Unity CPU/physics cost is not measurable from Python. These tests
model the soft-deadline policy and verify that no collider is skipped.
"""
import random
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text()


def slice_work(costs_ms, max_items, check_every, soft_ms):
    """Emulate a small-batch monotonic clock check after each completed item."""
    frames = []
    cursor = 0
    while cursor < len(costs_ms):
        frame = []
        used = 0.0
        while cursor < len(costs_ms):
            used += costs_ms[cursor]
            frame.append(cursor)
            cursor += 1
            if len(frame) >= max_items or (
                    len(frame) % check_every == 0 and used >= soft_ms):
                break
        frames.append((frame, used))
    return frames


class ColliderWallClockBudget(unittest.TestCase):
    def test_index_keeps_count_cap_and_soft_time_guard(self):
        body = SOURCE.split("private IEnumerator BuildGridAcrossFrames()", 1)[1].split(
            "private void IndexCollider(int i)", 1
        )[0]
        for token in (
            "AndroidIndexUpdatesPerFrame = 96",
            "AndroidIndexFrameBudgetSeconds = 0.002;",
            "AndroidIndexClockCheckInterval = 8;",
            "double frameStartedAt = Time.realtimeSinceStartupAsDouble;",
            "IndexCollider(indexCursor);",
            "indexCursor++;",
            "processed >= AndroidIndexUpdatesPerFrame",
            "processed % AndroidIndexClockCheckInterval == 0",
            "Time.realtimeSinceStartupAsDouble - frameStartedAt >=",
            "AndroidIndexFrameBudgetSeconds",
            "processed = 0;",
            "yield return null;",
            "frameStartedAt = Time.realtimeSinceStartupAsDouble;",
        ):
            self.assertIn(token, SOURCE if " = " in token else body)
        self.assertLess(body.index("indexCursor++;"),
                        body.index("processed >= AndroidIndexUpdatesPerFrame"))
        self.assertIn("yield return null;\n            deferredIndexBuild = null;\n            FinishIndexBuild();", body)

    def test_deactivation_avoids_sharing_the_first_refresh_frame(self):
        body = SOURCE.split("private IEnumerator DeactivateDistantAcrossFrames()", 1)[1].split(
            "private int Cell", 1
        )[0]
        self.assertIn("DeferredColliderUpdatesPerFrame = 24", SOURCE)
        self.assertIn("AndroidDeactivationBudgetSeconds = 0.0015;", SOURCE)
        self.assertIn("AndroidDeactivationClockCheckInterval = 4;", SOURCE)
        self.assertIn("bool constrainTime = Application.platform == RuntimePlatform.Android;", body)
        self.assertIn("if (constrainTime) yield return null;", body)
        self.assertLess(body.index("if (constrainTime) yield return null;"),
                        body.index("for (int i = 0; i < obstacles.Length; i++)"))
        self.assertIn("processed >= DeferredColliderUpdatesPerFrame", body)
        self.assertIn("constrainTime && processed % AndroidDeactivationClockCheckInterval == 0", body)
        self.assertIn("AndroidDeactivationBudgetSeconds", body)
        self.assertIn("if (constrainTime)\n                        frameStartedAt", body)
        self.assertIn("Vector3 framePosition = target != null ? target.position : default(Vector3);", body)
        self.assertEqual(body.count("target.position"), 2)
        self.assertIn("c.enabled = false;", body)

    def test_initial_solid_and_interrupted_resume_invariants(self):
        awake = SOURCE.split("private void Awake()", 1)[1].split(
            "private void BeginDeferredGridBuild()", 1
        )[0]
        finish = SOURCE.split("private void FinishIndexBuild()", 1)[1].split(
            "private void BeginDeferredDeactivation()", 1
        )[0]
        on = SOURCE.split("private void OnEnable()", 1)[1].split(
            "private void OnDisable()", 1
        )[0]
        self.assertIn("BeginDeferredGridBuild();", awake)
        self.assertNotIn("c.enabled = false;", awake)
        self.assertIn("indexReady = true;", finish)
        self.assertIn("RefreshNow();", finish)
        self.assertIn("BeginDeferredDeactivation();", finish)
        self.assertIn("if (!indexReady)", on)
        self.assertIn("BeginDeferredGridBuild();", on)
        self.assertIn("StopCoroutine(deferredIndexBuild);", SOURCE)
        self.assertIn("StopCoroutine(deferredDeactivation);", SOURCE)
        self.assertIn("obstacles[i].enabled = true;", SOURCE)

    def test_soft_deadline_handles_variable_native_cost_without_skipping(self):
        rng = random.Random(20261119)
        for max_items, check_every, soft_ms in ((96, 8, 2.0), (24, 4, 1.5)):
            # Most colliders cheap, a few slow native MeshCollider transforms.
            costs = [rng.uniform(0.02, 0.13) if i % 27 else
                     rng.uniform(0.55, 0.9) for i in range(1600)]
            frames = slice_work(costs, max_items, check_every, soft_ms)
            self.assertEqual(
                [i for ids, _ in frames for i in ids], list(range(len(costs)))
            )
            self.assertTrue(all(1 <= len(ids) <= max_items for ids, _ in frames))
            for ids, elapsed in frames:
                # A soft clock checked after a batch can overshoot by at
                # most one check interval's worth, not a hard deadline.
                self.assertLessEqual(len(ids), max_items)
                if len(ids) > check_every:
                    before_last_check = sum(costs[i] for i in ids[:-check_every])
                    self.assertLess(before_last_check, soft_ms + 1e-9)
            count_only_frames = (len(costs) + max_items - 1) // max_items
            self.assertGreaterEqual(len(frames), count_only_frames)
            self.assertTrue(any(len(ids) < max_items for ids, _ in frames))

    def test_editor_remains_count_only_for_deactivation(self):
        source = SOURCE.split("private IEnumerator DeactivateDistantAcrossFrames()", 1)[1].split(
            "private int Cell", 1
        )[0]
        self.assertIn("if (constrainTime) yield return null;", source)
        self.assertIn("(constrainTime && processed % AndroidDeactivationClockCheckInterval == 0", source)
        self.assertIn("if (constrainTime)\n                        frameStartedAt", source)


if __name__ == "__main__":
    unittest.main(verbosity=2)
