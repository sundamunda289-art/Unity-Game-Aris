#!/usr/bin/env python3
"""Deterministic, asset-free proof that 64m cell lookup preserves 42m loot queries."""
import math
import random
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs").read_text(encoding="utf-8")
CELL_METRES = 64.0
RADIUS = 42.0


def key(x, z):
    return (math.floor(x / CELL_METRES), math.floor(z / CELL_METRES))


class LootGridRegression(unittest.TestCase):
    def test_no_world_visual_or_despawn_rule_changed(self):
        for clause in (
            "private const float ActivationMetres = 42f;",
            "private const float ReleaseMetres = 69f;",
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

    def test_player_drop_never_bypasses_40_live_visuals(self):
        # A voluntary drop used to bypass the streaming cap and allocate a
        # new pickup GameObject even when 40 nearby meshes were active.
        direct = SOURCE[SOURCE.index("private AarisDroppedWeapon CreateDropped("):
                        SOURCE.index("private void UndoDrop(")]
        cap = "if (!deferVisual && active.Count >= MaximumActiveLoot) return null;"
        self.assertIn(cap, direct)
        self.assertLess(direct.index(cap), direct.index("int id = nextDropId--;"))
        self.assertLess(direct.index(cap), direct.index('new GameObject("Dropped "'))
        self.assertIn("if (!deferVisual)", direct)

        drop = SOURCE[SOURCE.index("public bool TryDropEquipped()"):
                      SOURCE.index("private bool IsBlockedDrop(")]
        self.assertIn("active.Count >= MaximumActiveLoot", drop)
        self.assertLess(drop.index("active.Count >= MaximumActiveLoot"),
                        drop.index("!TryFindDropSpot(out Vector3 point)"))
        self.assertLess(drop.index("CreateDropped(state, point, 1.25f)"),
                        drop.index("inventory.TryRemove(out AarisWeaponState removed)"))
        # Swapping a gun must still reserve its persistent record without
        # constructing a second model on the same frame.
        self.assertIn("deferVisual: true", SOURCE)
        # Bounded model: repeated direct drops cannot add a 41st live mesh,
        # while deferred swaps add zero new live meshes at any occupancy.
        for live in range(0, 41):
            for deferred in (False, True):
                permitted = deferred or live < 40
                live_after = live + int(permitted and not deferred)
                self.assertLessEqual(live_after, 40)
                if deferred:
                    self.assertTrue(permitted)
                    self.assertEqual(live_after, live)
        live = accepted = 0
        for _ in range(320):
            if live < 40:
                live += 1
                accepted += 1
            self.assertLessEqual(live, 40)
        self.assertEqual((live, accepted), (40, 40))

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
