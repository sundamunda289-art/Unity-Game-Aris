#!/usr/bin/env python3
"""Deterministic proof that 64m cell lookup preserves the 48m loot queries."""
import math
import random
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs").read_text(encoding="utf-8")
CELL_METRES = 64.0
RADIUS = 48.0


def key(x, z):
    return (math.floor(x / CELL_METRES), math.floor(z / CELL_METRES))


class LootGridRegression(unittest.TestCase):
    def test_no_world_visual_or_despawn_rule_changed(self):
        for clause in (
            "private const float ActivationMetres = 48f;",
            "private const float ReleaseMetres = 75f;",
            "private const int MaximumActiveLoot = 40;",
            "private const float CandidateCellMetres = 64f;",
            "IndexPlannedCandidates();",
            "CollectNearbyCandidateIndices(playerPosition);",
            "nearbyCandidateIndices.Sort();",
            "if (dx * dx + dz * dz >= loadSquared) continue;",
            "StreamFramesPerRefresh = 3;",
            "MaximumBuildAttemptsPerFrame = 2;",
            "MaximumCandidateProbesPerFrame = 6;",
            "probes == MaximumCandidateProbesPerFrame) return true;",
            "candidateCells.TryGetValue(CandidateCellKey(x, z)",
        ):
            self.assertIn(clause, SOURCE)

    def test_dense_floor_queries_keep_full_results_and_roof_safety(self):
        self.assertIn("floors = Physics.RaycastAll(", SOURCE)
        self.assertIn("candidate.collider.attachedRigidbody != null", SOURCE)
        self.assertIn("Mathf.Abs(candidate.point.y - expectedGround) > .35f", SOURCE)
        self.assertIn("candidate.normal.y < (indoor ? .966f : .94f)", SOURCE)

    def test_native_terrain_world_y_used_for_outdoor_floor(self):
        self.assertIn("terrain.SampleHeight(new Vector3(c.x, 0f, c.z))", SOURCE)
        self.assertNotIn("terrain.transform.position.y + terrain.SampleHeight", SOURCE)
        self.assertIn("float expectedGround = indoor ? c.y :", SOURCE)

    def test_all_points_in_circle_covered_and_priority_preserved(self):
        rng = random.Random(20261110)
        points = [(rng.uniform(-1000, 1000), rng.uniform(-1000, 1000))
                  for _ in range(750)]
        points += [(-999, -999), (999, 999), (0, 0), (64, 64),
                   (-64, -64), (42, 0), (-42, 0), (0, 42), (0, -42)]
        grid = {}
        for i, (x, z) in enumerate(points):
            grid.setdefault(key(x, z), []).append(i)

        player_positions = [(rng.uniform(-1000, 1000), rng.uniform(-1000, 1000))
                            for _ in range(250)]
        player_positions += [(0, 0), (64, 64), (-64, -64), (-999, -999),
                             (999, 999), (32, 32), (-32, -32), (63.99, 64)]
        sq = RADIUS * RADIUS
        for px, pz in player_positions:
            old = [i for i, (x, z) in enumerate(points)
                   if (x-px)**2 + (z-pz)**2 < sq]
            nearby = []
            min_x, max_x = math.floor((px-RADIUS)/CELL_METRES), math.floor((px+RADIUS)/CELL_METRES)
            min_z, max_z = math.floor((pz-RADIUS)/CELL_METRES), math.floor((pz+RADIUS)/CELL_METRES)
            for cx in range(min_x, max_x+1):
                for cz in range(min_z, max_z+1):
                    nearby.extend(grid.get((cx, cz), ()))
            nearby.sort()
            new = [i for i in nearby
                   if (points[i][0]-px)**2 + (points[i][1]-pz)**2 < sq]
            self.assertEqual(old, new, f"Mismatch at {px}, {pz}")
            self.assertEqual(len(nearby), len(set(nearby)), "Duplicate cell indices")


if __name__ == "__main__":
    unittest.main(verbosity=2)
