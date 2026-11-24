#!/usr/bin/env python3
"""Regression for post-movement AI shot reach, facing and cached-distance bugs.

These are source policy and math edge tests, not Android FPS measurements.
"""
import math
from pathlib import Path
import unittest

SRC = (Path(__file__).resolve().parents[2] /
       "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
RIVAL = SRC.split("internal sealed class AarisOfflineRival", 1)[1]
PHYSICAL = RIVAL.split("private void Update()", 1)[1].split(
    "private void TickVirtualCombat(", 1)[0]
VIRTUAL = RIVAL.split("private void TickVirtualCombat(", 1)[1].split(
    "private static float HorizontalShotDistanceSquared(", 1)[0]


def admission(shooter, face, target, reach):
    x = target[0] - shooter[0]
    z = target[2] - shooter[2]
    sq = x*x + z*z
    return sq <= reach*reach and (sq <= .0001 or
        face[0]*x + face[2]*z > .84*math.sqrt(sq))


class PostMovementBotShot(unittest.TestCase):
    def test_real_movement_borders(self):
        forward = (1., 0., 0.)
        origin = (0., 0., 0.)
        farther = (-.7, 0., 0.)
        closer = (.8, 0., 0.)
        target_inside = (37.8, 0., 0.)
        target_outside = (38.4, 0., 0.)
        self.assertTrue(admission(origin, forward, target_inside, 38.))
        self.assertFalse(admission(farther, forward, target_inside, 38.))
        self.assertFalse(admission(origin, forward, target_outside, 38.))
        self.assertTrue(admission(closer, forward, target_outside, 38.))
        self.assertTrue(admission((.4, 0., 0.), forward,
                                  (2.5, 0., 0.), 2.25))
        self.assertFalse(admission((-.4, 0., 0.), forward,
                                   (2.1, 0., 0.), 2.25))

    def test_facing_and_stance_height(self):
        self.assertFalse(admission((0, 0, 0), (-1, 0, 0),
                                   (4, 0, 0), 38.))
        self.assertTrue(admission((0, 0, 0), (1, 0, 0),
                                  (4, -5, 0), 38.))
        self.assertTrue(admission((0, 0, 0), (1, 0, 0),
                                  (0, 0, 0), 2.25))

    def test_physical_shot_uses_live_pose_after_character_controller(self):
        self.assertIn("IsPostMoveShotInRangeAndFacing(", PHYSICAL)
        self.assertLess(PHYSICAL.index("capsule.Move("),
                        PHYSICAL.index("IsPostMoveShotInRangeAndFacing("))
        shot = PHYSICAL.split("// Punch only within contact range", 1)[1].split(
            "if (carriedGun != null)", 1)[0]
        self.assertIn("afterMove, transform.forward,", shot)
        self.assertIn("target.position, carriedGun == null ? 2.25f : 38f", shot)
        self.assertIn("owner.MayAttackPlayer(this)", shot)
        self.assertIn("TryGetSightHit(target, out RaycastHit shotHit)", shot)
        self.assertIn("MuzzlePathClear(target)", shot)
        self.assertNotIn("distance <= (carriedGun == null ? 2.25f : 38f)", shot)
        self.assertIn("distanceSqr <= reach * reach", RIVAL)
        self.assertIn("Vector3.Dot(forward, delta)", RIVAL)

    def test_virtual_shot_uses_actual_post_motion_position(self):
        self.assertLess(VIRTUAL.index("selfPosition = next;"),
                        VIRTUAL.index("HorizontalShotDistanceSquared(selfPosition, targetPosition)"))
        self.assertLess(VIRTUAL.index("HorizontalShotDistanceSquared(selfPosition, targetPosition)"),
                        VIRTUAL.index("owner.IsVirtualShotBlocked(selfPosition, targetPosition)"))
        self.assertIn("shotDistanceSqr > shotReach * shotReach", VIRTUAL)
        self.assertIn("Mathf.Sqrt(shotDistanceSqr) / 38f", VIRTUAL)
        self.assertNotIn("distance > (carriedGun == null ? 2.25f : 38f)", VIRTUAL)
        for unchanged in ("targetHealth.ApplyDamage(damage, gameObject);",
                          "if (!cachedVirtualDamageRival.virtualBody)",
                          "(targetPosition - playerPosition).sqrMagnitude",
                          "owner.IsVirtualShotBlocked(selfPosition, targetPosition)"):
            self.assertIn(unchanged, VIRTUAL)

    def test_combat_100_rivals_and_distance_cadence_unchanged(self):
        for token in (
            "private const int RivalCount = 100;",
            "private const float FarSimulationInterval = 0.35f;",
            "return bot == attackerOne || bot == attackerTwo;",
            "float hitChance = carriedGun != null",
            "if (carriedGun != null) carriedMagazine--;"):
            self.assertIn(token, SRC)


if __name__ == "__main__":
    unittest.main()
