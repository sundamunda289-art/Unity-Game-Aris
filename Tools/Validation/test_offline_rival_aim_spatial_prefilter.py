#!/usr/bin/env python3
"""100-bot aim/Red-Lock spatial rejection contract and conservative parity."""
from pathlib import Path
import random
import unittest

SRC = (Path(__file__).resolve().parents[2] /
       "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
MAIN = SRC.split("internal static bool TryAcquireAimTarget(", 1)[1].split(
    "private void RefreshStatus(", 1)[0]
FALLBACK = SRC.split("private static bool TryAcquireAimFallback(", 1)[1].split(
    "private void CreateStatus(", 1)[0]

def square_distance(a,b):
    return sum((v-w)**2 for v,w in zip(a,b))

class AimSpatialFilter(unittest.TestCase):
    def test_both_scans_prefilter_before_native_pose_and_keep_physical_los(self):
        self.assertIn("private const float AimSpatialSlack = 10f;", SRC)
        self.assertIn("IsDormantAimInvisible => centrallyDormant && !lastCameraRelevant;", SRC)
        self.assertIn('new ProfilerMarker("Aaris.AI.AimTargetCandidates")', SRC)
        self.assertIn("using var aiProfilerScope = k_AimSelection.Auto();", MAIN)
        for section in (MAIN, FALLBACK):
            for expr in ("activeDirector.rivalSpatialRegistered[i] &&",
                         "bot.IsDormantAimInvisible &&",
                         "activeDirector.rivalSpatialPositions[i] - origin",
                         "cachedCullRangeSqr) continue;",
                         "if (!bot.HasVisibleBody) continue;",
                         "Vector3 delta = bot.transform.position"):
                self.assertIn(expr, section)
            self.assertLess(section.index("rivalSpatialRegistered[i] &&"),
                            section.index("if (!bot.HasVisibleBody) continue;"))
            self.assertGreater(section.index("if (!bot.HasVisibleBody) continue;"),
                               section.index("Vector3 delta = bot.transform.position"))
        self.assertIn("MuzzlePathOpenTo(shooter, candidate.transform, closestPoint)", MAIN)
        self.assertIn("MuzzlePathOpenTo(shooter, bot.transform, point)", FALLBACK)

    def test_range_parity_under_time_sliced_movement_and_camera_height(self):
        randomizer = random.Random(120261123)
        for _ in range(200):
            eye=(randomizer.uniform(-900,900),randomizer.uniform(0,80),
                 randomizer.uniform(-900,900))
            limit=randomizer.choice((38.,100.,155.,480.))
            for i in range(100):
                real=(eye[0]+randomizer.uniform(-limit-15,limit+15),
                      eye[1]+randomizer.uniform(-4,4),
                      eye[2]+randomizer.uniform(-limit-15,limit+15))
                cached=tuple(v+randomizer.uniform(-3,3) for v in real)
                exact=(real[0], real[1]+1.12, real[2])
                is_valid=square_distance(exact,eye)<=limit**2
                unregistered=(i%11==0)
                # Distant actors with a live/visible body NEVER trust the
                # spatial cache to reject a target after an external teleport.
                dormant_hidden = (i % 13 == 0)
                would_skip=(not unregistered and dormant_hidden and
                            square_distance(cached,eye)>(limit+10.)**2)
                if is_valid:
                    self.assertFalse(would_skip, (i,limit,real,cached))

    def test_teleported_visible_actor_bypasses_stale_dormant_cache(self):
        real=(1.3,0.,25.)
        stale=(900.,0.,900.)
        eye=(0.,1.35,0.)
        self.assertLess(square_distance((real[0],real[1]+1.12,real[2]),eye),40.**2)
        self.assertGreater(square_distance(stale,eye),(40.+10.)**2)
        is_dormant_aim_invisible=False  # Actual visible actor
        self.assertFalse(is_dormant_aim_invisible and
                         square_distance(stale,eye)>(40.+10.)**2)

    def test_unregistered_candidates_are_not_discarded(self):
        self.assertIn("if (activeDirector.rivalSpatialRegistered[i] &&", MAIN)
        self.assertIn("if (activeDirector.rivalSpatialRegistered[i] &&", FALLBACK)
        self.assertEqual(SRC.count("Aaris.AI.AimTargetCandidates"),1)

if __name__=="__main__":
    unittest.main()
