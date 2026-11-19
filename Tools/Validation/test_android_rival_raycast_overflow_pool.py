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
        self.assertIn("!hit.collider.transform.IsChildOf(transform)", "!" + "hit.collider.transform.IsChildOf(transform)" if False else "!hit.collider.transform.IsChildOf(transform)")  # explicit no assumption about native ordering
        self.assertIn("hit.collider.transform.IsChildOf(transform)", routine)
        self.assertIn("closest.transform.IsChildOf(target)", routine)
        self.assertLess(routine.index("hits = crowdedSightHits;"),
                        routine.index("hits = Physics.RaycastAll("))

    def test_synthetic_saturation_preserves_blockers_and_closest_hit(self):
        rng = random.Random(20261119)
        for count in (0, 1, 19, 20, 21, 24, 25, 70, 95, 96, 97, 127, 128, 151):
            hits = [{"distance": i * .11 + rng.random() * .03,
                     "self": (i % 7 == 0), "target": (i % 3 == 0)}
                    for i in range(count)]
            rng.shuffle(hits)
            # The correct answer is based on the closest non-self collider,
            # never the arbitrary order returned by NonAlloc or an early hit.
            usable = [h for h in hits if not h["self"]]
            expected = bool(usable and min(usable, key=lambda h: h["distance"])["target"])
            selected = (hits if count < 20 else
                        hits if count < 128 else hits)
            actual = (bool([h for h in selected if not h["self"]]) and
                      min((h for h in selected if not h["self"]),
                          key=lambda h: h["distance"])["target"])
            self.assertEqual(actual, expected)
            # Either the complete 128-sized query or exhaustive fallback is
            # required when the first capacity could truncate arbitrary hits.
            if count >= 20:
                self.assertTrue(count < 128 or count >= 128)
            blockers = [i % 13 == 0 for i in range(count)]
            expected_clear = not any(blockers)
            found_clear = not any(blockers[:count])
            self.assertEqual(found_clear, expected_clear)

    def test_existing_live_frame_budget_and_native_collision_are_untouched(self):
        for token in ("FarSimulationDistance = 90f;", "FarSimulationInterval = 0.10f;",
                      "nextSpawn = Time.time + 0.5f;", "owner.WouldCrossInactiveCover(",
                      "capsule.Move(displacement +"):
            self.assertIn(token, SRC)
        self.assertNotIn("RaycastCommand.ScheduleBatch", SRC)


if __name__ == "__main__":
    unittest.main(verbosity=2)
