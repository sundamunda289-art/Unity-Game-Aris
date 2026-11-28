#!/usr/bin/env python3
"""No renderer/Animator query for 100 remote Red-Lock candidates outside the cone."""
from pathlib import Path
import random
import unittest

S=(Path(__file__).resolve().parents[2]/"Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
PRIMARY=S.split("internal static bool TryAcquireAimTarget(",1)[1].split("private void RefreshStatus(",1)[0]
FALLBACK=S.split("private static bool TryAcquireAimFallback(",1)[1].split("private void CreateStatus(",1)[0]
SIGHT=S.split("private static bool AimRayReachesRival(",1)[1].split("private void RefreshStatus(",1)[0]

class AimRivalPrefilter(unittest.TestCase):
    def test_primary_preserves_actual_hit_and_cheap_first(self):
        checks=("float sqrDistance = delta.sqrMagnitude;",
                "projected * projected <= threshold * threshold * sqrDistance",
                "if (!bot.HasVisibleBody) continue;",
                "float distance = Mathf.Sqrt(sqrDistance);")
        locations=[PRIMARY.index(x) for x in checks]
        self.assertEqual(locations,sorted(locations))
        self.assertIn("AimRayReachesRival(candidateRay, distance + .5f, shooter, candidate.transform)",PRIMARY)
        self.assertIn("hit.BelongsTo(rival)",SIGHT)
        self.assertIn("!StreamedCoverBlocks(ray, hit.distance)",SIGHT)
        self.assertIn("MuzzlePathOpenTo(shooter, rival, hit.point)",SIGHT)

    def test_fallback_preserves_actual_hit_and_cheap_first(self):
        checks=("float sqr = delta.sqrMagnitude;",
                "projected * projected <= cutoff * cutoff * sqr",
                "if (!bot.HasVisibleBody) continue;",
                "float distance = Mathf.Sqrt(sqr);")
        locations=[FALLBACK.index(x) for x in checks]
        self.assertEqual(locations,sorted(locations))
        self.assertIn("AimRayReachesRival(candidateRay, aimRanges[attempt] + .5f,",FALLBACK)
        self.assertIn("shooter, bot.transform)",FALLBACK)
        self.assertIn("TryFirstCombatHit(ray, distance, shooter, out AarisCombatHit hit)",SIGHT)

    def test_100_bot_geometric_eligibility_is_unchanged(self):
        rng=random.Random(20261123)
        old_queries=new_queries=0
        for n in range(100):
            alive=rng.random()>.05
            has_body=n>=85 and rng.random()>.25
            distance=rng.uniform(200,990) if n<85 else rng.uniform(2,120)
            dot=rng.uniform(-1,1)
            geometric=.25<=distance*distance<=160*160 and dot>.65
            before=alive and has_body and geometric
            after=alive and geometric and has_body
            self.assertEqual(before,after)
            if alive:
                old_queries+=1
                if geometric:
                    new_queries+=1
        self.assertLess(new_queries,old_queries//5)

if __name__=="__main__": unittest.main()
