#!/usr/bin/env python3
"""Static + deterministic spatial-index parity; not a Unity Android profiler."""
import math
import random
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text()


def overlap(a, b):
    """XZ inclusive AABB intersection for authored collider/query bounds."""
    return a[0] <= b[2] and a[2] >= b[0] and a[1] <= b[3] and a[3] >= b[1]


def indexed_candidates(rectangles, query, cell_size=250, limit=64):
    grid = {}
    overflow = []
    for i, (x0, z0, x1, z1) in enumerate(rectangles):
        first_x, last_x = math.floor(x0 / cell_size), math.floor(x1 / cell_size)
        first_z, last_z = math.floor(z0 / cell_size), math.floor(z1 / cell_size)
        width, depth = last_x - first_x + 1, last_z - first_z + 1
        if width > limit or depth > limit or width * depth > limit:
            overflow.append(i)
            continue
        for cx in range(first_x, last_x + 1):
            for cz in range(first_z, last_z + 1):
                grid.setdefault((cx, cz), []).append(i)
    q0, p0, q1, p1 = query
    found = set(overflow)
    for cx in range(math.floor(q0 / cell_size), math.floor(q1 / cell_size) + 1):
        for cz in range(math.floor(p0 / cell_size), math.floor(p1 / cell_size) + 1):
            found.update(grid.get((cx, cz), ()))
    return {i for i in found if overlap(rectangles[i], query)}, grid, overflow


class OversizedColliderGrid(unittest.TestCase):
    def test_single_collider_can_never_expand_unbounded_cells(self):
        index = SOURCE.split("private void IndexCollider(int i)", 1)[1].split(
            "private void FinishIndexBuild()", 1
        )[0]
        self.assertIn("private const int MaxCellsPerCollider = 64;", SOURCE)
        self.assertIn("long width = (long)lastX - firstX + 1;", index)
        self.assertIn("long depth = (long)lastZ - firstZ + 1;", index)
        self.assertIn("width * depth > MaxCellsPerCollider", index)
        self.assertIn("oversizedIndices.Add(i);", index)
        self.assertLess(index.index("oversizedIndices.Add(i);"),
                        index.index("for (long xCell = firstX;"))
        self.assertIn("for (long xCell = firstX;", index)
        self.assertIn("for (long zCell = firstZ;", index)
        self.assertIn("entries.Add(i);", index)
        self.assertIn("streamed[i] = false;", index)
        self.assertIn("!Finite(min) || !Finite(max)", index)

    def test_overflow_is_in_both_runtime_queries(self):
        refresh = SOURCE.split("private void RefreshNow(bool forcePhysicsSync)", 1)[1].split(
            "internal bool HasPotentialInactiveObstacle", 1
        )[0]
        probe = SOURCE.split("internal bool HasPotentialInactiveObstacle", 1)[1].split(
            "private void OnEnable()", 1
        )[0]
        for body in (refresh, probe):
            self.assertIn("foreach (int i in oversizedIndices)", body)
            self.assertIn("Collider c = obstacles[i];", body)
        self.assertIn("wanted[i] = query;", refresh)
        self.assertIn("if (!c.enabled) c.enabled = true;", refresh)
        self.assertIn("nextActive.Add(i);", refresh)
        self.assertIn("if (visited[i] == query) continue;", refresh)
        self.assertIn("worldBounds[i].Intersects(probe)", probe)
        self.assertIn("streamed[i] && c != null && !c.enabled", probe)
        self.assertIn("probeVisited[i] = probeStamp;", probe)
        self.assertIn("if (forcePhysicsSync) Physics.SyncTransforms();", refresh)

    def test_index_parity_including_massive_bridge_and_seams(self):
        rng = random.Random(20261119)
        rectangles = []
        for _ in range(700):
            cx, cz = rng.uniform(-1200, 1200), rng.uniform(-1200, 1200)
            ex, ez = rng.uniform(.1, 200), rng.uniform(.1, 200)
            rectangles.append((cx - ex, cz - ez, cx + ex, cz + ez))
        rectangles += [
            (-6000, -6000, 6000, 6000),  # 49x49 cells: overflow, no 2401-cell burst
            (-15000, -1, 15000, 1),      # 121 cells: long authored pier
            (-1000, -1000, 1000, 1000),  # crosses 81 cells: wide foundation
            (0, 0, 250, 250),            # touches all four cell boundaries
            (-250, -250, 0, 0),
        ]
        for _ in range(80):
            x, z = rng.uniform(-1300, 1300), rng.uniform(-1300, 1300)
            r = rng.uniform(.01, 32)
            q = (x - r, z - r, x + r, z + r)
            selected, grid, oversized = indexed_candidates(rectangles, q)
            exhaustive = {i for i, rect in enumerate(rectangles) if overlap(rect, q)}
            self.assertEqual(selected, exhaustive)
            self.assertTrue(all(len(entries) > 0 for entries in grid.values()))
            self.assertEqual(set(oversized), {700, 701, 702})

    def test_collision_streaming_lifecycle_stays_intact(self):
        self.assertIn("if (!indexReady) return;", SOURCE)
        self.assertIn("if (!indexReady) return false;", SOURCE)
        self.assertIn("BeginDeferredGridBuild();", SOURCE)
        self.assertIn("if (processed >= AndroidIndexUpdatesPerFrame ||", SOURCE)
        self.assertIn("if (processed >= DeferredColliderUpdatesPerFrame ||", SOURCE)
        self.assertIn("obstacles[i].enabled = true;", SOURCE)
        self.assertIn("streamed[i] && c != null && c.enabled", SOURCE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
