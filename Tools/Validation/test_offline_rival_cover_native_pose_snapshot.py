#!/usr/bin/env python3
"""Native physical cover query uses one self and one shooter position."""
import random
import unittest
from pathlib import Path
S=(Path(__file__).resolve().parents[2]/"Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
C=S.split("private void OnDamaged(",1)[1].split("internal void TakeZoneDamage(",1)[0]

class CoverNativePose(unittest.TestCase):
    def test_single_live_pose_per_real_cover_query(self):
        for x in ("if (virtualBody || Time.time < nextCoverQuery) return;",
                  "Transform selfTransform = transform;",
                  "Vector3 selfPosition = selfTransform.position;",
                  "Vector3 attackerPosition = attacker.transform.position;",
                  "Vector3 away = selfPosition - attackerPosition;",
                  "Vector3 coverSearchCenter = selfPosition + Vector3.up;",
                  "obstacle.transform.IsChildOf(selfTransform)",
                  "Vector3 coverAxis = bounds.center - attackerPosition;",
                  "candidate.y = selfPosition.y;",
                  "(candidate - selfPosition).sqrMagnitude",
                  "Vector3 coverFrom = attackerPosition + Vector3.up * 1.25f;"):
            self.assertIn(x,C)
        self.assertEqual(C.count("attacker.transform.position"),1)
        self.assertEqual(C.count("selfTransform.position"),1)
        self.assertNotIn("away.Normalize();",C)
        self.assertLess(C.index("if (virtualBody || Time.time < nextCoverQuery) return;"),
                        C.index("Vector3 selfPosition = selfTransform.position;"))

    def test_native_collision_cover_and_timing_unchanged(self):
        for x in ("nextHitReaction = Time.time + 0.22f;",
                  "nextCoverQuery = Time.time + 1.5f;",
                  "Physics.OverlapSphereNonAlloc(coverSearchCenter,",
                  "11f, coverCandidates, Physics.DefaultRaycastLayers",
                  "11f, crowdedCoverCandidates, Physics.DefaultRaycastLayers",
                  "obstacle is TerrainCollider",
                  "obstacle is CharacterController",
                  "bounds.size.y < .9f || bounds.size.y > 18f",
                  "if (coverAxis.sqrMagnitude < .01f) continue;",
                  "if (cost < 2f || cost > 160f || cost >= best) continue;",
                  "obstacle.Raycast(new Ray(coverFrom, coverRay / coverDistance)",
                  "coverDestination = candidate;",
                  "coverUntil = Time.time + 2f;",
                  "if (away.sqrMagnitude < .01f) return;"):
            self.assertIn(x,C)

    def test_100_rivals_snapshot_cost_parity(self):
        rng=random.Random(20261123)
        for tick in range(70):
            for rival in range(100):
                me=[rng.uniform(-850,850) for _ in range(3)]
                shooter=[v+rng.uniform(-10,10) for v in me]
                if (me[0]-shooter[0])**2+(me[2]-shooter[2])**2<.01:continue
                captured=tuple(me)
                for _ in range(6):
                    candidate=[v+rng.uniform(-12,12) for v in me]
                    candidate[1]=me[1]
                    old=sum((a-b)**2 for a,b in zip(candidate,me))
                    new=sum((a-b)**2 for a,b in zip(candidate,captured))
                    self.assertEqual(old,new)
                    self.assertEqual(2<=old<=160,2<=new<=160)

if __name__=="__main__":
    unittest.main()
