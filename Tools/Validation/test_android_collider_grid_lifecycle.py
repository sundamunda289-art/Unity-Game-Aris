#!/usr/bin/env python3
"""CPU-only source and indexing parity tests; not Unity Android Play Mode tests."""
import math
import random
import unittest
from pathlib import Path

CODE = (Path(__file__).resolve().parents[2] / "Assets" / "AarisWorld" /
        "Scripts" / "AarisNearbyColliders.cs").read_text(encoding="utf-8")


def cells_for_bounds(center_x, center_z, extent_x, extent_z, size=250):
    x0 = math.floor((center_x - extent_x) / size)
    x1 = math.floor((center_x + extent_x) / size)
    z0 = math.floor((center_z - extent_z) / size)
    z1 = math.floor((center_z + extent_z) / size)
    return [(x, z) for x in range(x0, x1 + 1)
            for z in range(z0, z1 + 1)]


def index_rectangles(rects, budget=None):
    cells = {}
    cursor = 0
    while cursor < len(rects):
        stop = len(rects) if budget is None else min(cursor + budget, len(rects))
        while cursor < stop:
            (x, z, ex, ez, active, supported) = rects[cursor]
            if active and supported:
                for cell in cells_for_bounds(x, z, ex, ez):
                    cells.setdefault(cell, []).append(cursor)
            cursor += 1
    return cells


class AndroidColliderGridLifecycle(unittest.TestCase):
    def test_all_original_solids_stay_live_until_grid_finished(self):
        awake = CODE.split("private void Awake()", 1)[1].split(
            "private void BeginDeferredGridBuild()", 1)[0]
        finish = CODE.split("private void FinishIndexBuild()", 1)[1].split(
            "private void BeginDeferredDeactivation()", 1)[0]
        self.assertIn("Application.platform == RuntimePlatform.Android", awake)
        self.assertIn("BeginDeferredGridBuild();", awake)
        self.assertIn("return;", awake)
        self.assertNotIn("c.enabled = false;", awake)
        self.assertNotIn("BeginDeferredDeactivation();", awake)
        self.assertIn("indexReady = true;", finish)
        self.assertIn("RefreshNow();", finish)
        self.assertIn("BeginDeferredDeactivation();", finish)

    def test_incomplete_index_cannot_stream_or_answer_stale_probes(self):
        update = CODE.split("private void Update()", 1)[1].split(
            "public void RefreshNow()", 1)[0]
        refresh = CODE.split("public void RefreshNow()", 1)[1].split(
            "private void RefreshNow(bool forcePhysicsSync)", 1)[0]
        probe = CODE.split("internal bool HasPotentialInactiveObstacle(", 1)[1].split(
            "private void OnEnable()", 1)[0]
        self.assertIn("if (!indexReady) return;", update)
        self.assertIn("if (!indexReady) return;", refresh)
        self.assertIn("if (!indexReady) return false;", probe)

    def test_resuming_after_disable_does_not_duplicate_cached_colliders(self):
        build = CODE.split("private IEnumerator BuildGridAcrossFrames()", 1)[1].split(
            "private void IndexCollider(int i)", 1)[0]
        on = CODE.split("private void OnEnable()", 1)[1].split(
            "private void OnDisable()", 1)[0]
        off = CODE.split("private void OnDisable()", 1)[1]
        self.assertIn("while (indexCursor < obstacles.Length)", build)
        self.assertLess(build.index("indexCursor++;"),
                        build.index("yield return null;", build.index("IndexCollider(indexCursor);")))
        self.assertIn("BeginDeferredGridBuild();", on)
        self.assertIn("if (!indexReady)", on)
        self.assertIn("StopCoroutine(deferredIndexBuild);", off)
        self.assertIn("StopCoroutine(deferredDeactivation);", off)

    def test_bounded_full_grid_matches_eager_cell_membership(self):
        rng = random.Random(20261119)
        rectangles = [(rng.uniform(-1100,1100), rng.uniform(-1100,1100),
                       rng.uniform(1,230), rng.uniform(1,230),
                       rng.random() > .04, rng.random() > .1)
                      for _ in range(1550)]
        # Long structures intersect several cells, and seam touches count.
        rectangles += [(-125, 125, 375, 4, True, True),
                       (0, 0, 250, 250, True, True),
                       (0, 0, 4, 4, False, True)]
        expected = index_rectangles(rectangles)
        for budget in (1, 24, 96, 256, 3000):
            self.assertEqual(expected, index_rectangles(rectangles, budget))
        self.assertEqual(len(set(expected.get((0,0),[]))),
                         len(expected.get((0,0),[])))


if __name__ == "__main__":
    unittest.main(verbosity=2)
