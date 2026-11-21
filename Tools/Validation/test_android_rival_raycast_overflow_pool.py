#!/usr/bin/env python3
"""Regression for cached dense-scenery rival physics queries.

Source + synthetic parity only: no Unity physics or Android timing claims.
"""
from pathlib import Path
import random
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
MANAGER = SRC.split("internal sealed class AarisOfflineRival", 1)[0]
RIVAL = SRC.split("internal sealed class AarisOfflineRival", 1)[1]


class RivalNonAllocOverflow(unittest.TestCase):
    def test_spawn_overlap_reuses_crowded_buffer_before_allocation(self):
        self.assertIn("private readonly Collider[] spawnHits = new Collider[24];", MANAGER)
        self.assertIn("private readonly Collider[] crowdedSpawnHits = new Collider[96];", MANAGER)
        routine = MANAGER.split("private bool SpawnClear(Vector3 point)", 1)[1].split(
            "internal bool WouldCrossInactiveCover", 1
        )[0]
        self.assertIn("Physics.OverlapCapsuleNonAlloc(low, high, 0.34f,\n                spawnHits,", routine)
        self.assertIn("hits = crowdedSpawnHits;", routine)
        self.assertIn("count = Physics.OverlapCapsuleNonAlloc(low, high, 0.34f,\n                    crowdedSpawnHits,", routine)
        self.assertIn("if (count >= crowdedSpawnHits.Length)", routine)
        self.assertIn("hits = Physics.OverlapCapsule(low, high, 0.34f,", routine)
        self.assertIn("if (hits[i] != null && !(hits[i] is TerrainCollider)) return false;", routine)
        self.assertLess(routine.index("hits = crowdedSpawnHits;"),
                        routine.index("hits = Physics.OverlapCapsule("))

    def test_los_reuses_crowded_buffer_before_allocating_exhaustive(self):
        self.assertIn("private readonly RaycastHit[] sightHits = new RaycastHit[20];", RIVAL)
        self.assertIn("private readonly RaycastHit[] crowdedSightHits = new RaycastHit[128];", RIVAL)
        routine = RIVAL.split("private bool HasSight()", 1)[1].split(
            "internal void TakeZoneDamage", 1
        )[0]
        self.assertIn("Physics.RaycastNonAlloc(ray, sightHits, distance,", routine)
        self.assertIn("hits = crowdedSightHits;", routine)
        self.assertIn("Physics.RaycastNonAlloc(ray, crowdedSightHits, distance,", routine)
        self.assertIn("if (count >= crowdedSightHits.Length)", routine)
        self.assertIn("hits = Physics.RaycastAll(ray, distance,", routine)
        self.assertIn("if (hit.distance < first)", routine)
        self.assertIn("hit.collider.transform.IsChildOf(transform)", routine)
        self.assertIn("closest.transform.IsChildOf(victim)", routine)
        self.assertLess(routine.index("hits = crowdedSightHits;"),
                        routine.index("hits = Physics.RaycastAll("))

    def test_synthetic_saturation_preserves_blockers_and_closest_hit(self):
        rng = random.Random(20261119)

        def staged_query(contacts, small, large):
            first = contacts[:small]
            if len(first) < small:
                return first, "small"
            second = contacts[:large]
            if len(second) < large:
                return second, "pooled"
            return contacts, "exhaustive"

        for count in (0, 1, 19, 20, 21, 24, 25, 70, 95, 96, 97, 127, 128, 151):
            hits = [{"distance": i * .11 + rng.random() * .03,
                     "self": i % 7 == 0, "target": i % 3 == 0}
                    for i in range(count)]
            rng.shuffle(hits)
            selected, stage = staged_query(hits, 20, 128)
            self.assertEqual(len(selected), count)
            self.assertEqual(stage, "small" if count < 20 else
                             "pooled" if count < 128 else "exhaustive")
            usable = [h for h in hits if not h["self"]]
            expected = bool(usable and min(usable, key=lambda h: h["distance"])["target"])
            observed = [h for h in selected if not h["self"]]
            actual = bool(observed and min(observed, key=lambda h: h["distance"])["target"])
            self.assertEqual(actual, expected)

            blockers = [i % 13 == 0 for i in range(count)]
            used, overlap_stage = staged_query(blockers, 24, 96)
            self.assertEqual(len(used), count)
            self.assertEqual(overlap_stage, "small" if count < 24 else
                             "pooled" if count < 96 else "exhaustive")
            self.assertEqual(not any(used), not any(blockers))

        # A saturated, unordered first buffer may omit a nearer wall.
        dense = [{"distance": float(100 - i), "self": False, "target": True}
                 for i in range(21)]
        dense[-1]["target"] = False
        selected, stage = staged_query(dense, 20, 128)
        self.assertEqual(stage, "pooled")
        self.assertFalse(min(selected, key=lambda h: h["distance"])["target"])

    def test_existing_live_frame_budget_and_native_collision_are_untouched(self):
        for token in ("FarSimulationDistance = 90f;", "FarSimulationInterval = 0.35f;",
                      "nextSpawn = Time.time + 0.17f;", "owner.WouldCrossInactiveCover(",
                      "capsule.Move(displacement +"):
            self.assertIn(token, SRC)
        self.assertNotIn("RaycastCommand.ScheduleBatch", SRC)


if __name__ == "__main__":
    unittest.main(verbosity=2)
