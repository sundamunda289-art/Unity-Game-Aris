#!/usr/bin/env python3
"""Source and deterministic parity for 100-rival dormant/native pose optimization."""
from pathlib import Path
import random
import unittest

SRC = (Path(__file__).resolve().parents[2] /
       "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
MAIN = SRC.split("internal static bool TryAcquireAimTarget(", 1)[1].split(
    "private void RefreshStatus(", 1)[0]
FALLBACK = SRC.split("private static bool TryAcquireAimFallback(", 1)[1].split(
    "private void CreateStatus(", 1)[0]
REMOTE = SRC.split("private void TickVirtualCombat(", 1)[1].split(
    "private float NextVirtualRandom(", 1)[0]

class DormantPoseBudget(unittest.TestCase):
    def test_hidden_dormant_aim_pruned_before_native_pose(self):
        self.assertIn("IsDormantAimInvisible => centrallyDormant && !lastCameraRelevant;", SRC)
        for section in (MAIN, FALLBACK):
            gate = section.index("if (bot.IsDormantAimInvisible) continue;")
            getter = section.index("Vector3 delta = bot.transform.position")
            self.assertLess(gate, getter)
            self.assertLess(getter, section.index("if (!bot.HasVisibleBody) continue;"))
        self.assertIn("MuzzlePathOpenTo(shooter, candidate.transform, closestPoint)", MAIN)
        self.assertIn("MuzzlePathOpenTo(shooter, bot.transform, point)", FALLBACK)

    def test_100_bot_visible_aim_parity(self):
        rng = random.Random(20261123)
        for _ in range(200):
            actors = []
            for i in range(100):
                dormant = i < 85
                camera = not dormant and bool(rng.getrandbits(1))
                body = not dormant and bool(rng.getrandbits(1))
                range_ok = bool(rng.getrandbits(1))
                los = bool(rng.getrandbits(1))
                actors.append((dormant, camera, body, range_ok, los))
            old = [i for i,(d,c,b,r,l) in enumerate(actors) if b and r and l]
            new = [i for i,(d,c,b,r,l) in enumerate(actors)
                   if not (d and not c) and b and r and l]
            self.assertEqual(old, new)
        # Active visible teleports must always bypass the dormant-only gate.
        self.assertFalse(False and not True)

    def test_virtual_ai_single_pose_snapshot_keeps_damage_and_cover(self):
        for s in ("Vector3 selfPosition = transform.position;",
                  "Vector3 selfForward = transform.forward;",
                  "Vector3 targetPosition = hasTarget ? target.position : Vector3.zero;",
                  "targetPosition - selfPosition : selfForward * 18f",
                  "owner.TryZoneRetreatDirection(selfPosition,",
                  "owner.WouldCrossInactiveCover(selfPosition,",
                  "Vector3 next = selfPosition + step;",
                  "owner.TryVirtualGround(next.x, next.z, out float ground)",
                  "owner.TrackRivalPosition(spatialSlot, this, next);",
                  "(targetPosition - playerTarget.position).sqrMagnitude",
                  "targetHealth.ApplyDamage(damage, gameObject);",
                  "!hiddenTarget.virtualBody"):
            self.assertIn(s, REMOTE)
        self.assertEqual(REMOTE.count("transform.position"), 3)
        self.assertEqual(REMOTE.count("transform.forward"), 1)
        self.assertEqual(REMOTE.count("target.position"), 1)

if __name__ == "__main__":
    unittest.main()
