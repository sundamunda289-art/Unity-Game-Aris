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
SHOT_ADMISSION = RIVAL.split("private static bool IsPostMoveShotInRangeAndFacing(", 1)[1].split(
    "private float NextVirtualRandom(", 1)[0]
FIRE_RANGE = RIVAL.split("internal static float TacticalFireRange(", 1)[1].split(
    "internal static Vector3 StepLocomotionVelocity(", 1)[0]


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
        # Movement must cross the actual weapon's boundary, including a
        # shorter authored range, instead of retaining the former 38m cap.
        for reach in (12., 22., 30., 38., 40., 65., 80.):
            with self.subTest(reach=reach):
                target_inside = (reach - .2, 0., 0.)
                target_outside = (reach + .4, 0., 0.)
                self.assertTrue(admission(origin, forward, target_inside, reach))
                self.assertFalse(admission(farther, forward, target_inside, reach))
                self.assertFalse(admission(origin, forward, target_outside, reach))
                self.assertTrue(admission(closer, forward, target_outside, reach))
                self.assertTrue(admission(origin, forward, (reach, 0., 0.), reach))
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
                        PHYSICAL.index("Vector3 afterMove = transform.position;"))
        self.assertLess(PHYSICAL.index("Vector3 afterMove = transform.position;"),
                        PHYSICAL.index("IsPostMoveShotInRangeAndFacing("))
        shot = PHYSICAL.split("// Punch only within contact range", 1)[1].split(
            "if (carriedGun != null)", 1)[0]
        self.assertIn("afterMove, transform.forward,", shot)
        self.assertIn("target.position, FireRange)", shot)
        self.assertIn("internal float FireRange => TacticalFireRange(carriedGun);", RIVAL)
        self.assertIn("if (gun == null) return 2.25f;", FIRE_RANGE)
        self.assertIn("Mathf.Min(tactical, Mathf.Max(2.25f, gun.rangeMetres))", FIRE_RANGE)
        self.assertIn("(carriedGun == null || settledForShot)", shot)
        self.assertIn("(target != playerTarget || owner.MayAttackPlayer(this))", shot)
        self.assertIn("Time.time >= nextAttack", shot)
        self.assertIn("TryGetSightHit(target, out RaycastHit shotHit)", shot)
        self.assertIn("MuzzlePathClear(target)", shot)
        self.assertNotRegex(shot, r"\bdistance(?:Sqr)?\s*(?:<=|>=|<|>)")
        self.assertIn("Vector3 delta = to - from;", SHOT_ADMISSION)
        self.assertIn("delta.y = 0f;", SHOT_ADMISSION)
        self.assertIn("distanceSqr <= reach * reach", SHOT_ADMISSION)
        self.assertIn("distanceSqr <= 0.0001f", SHOT_ADMISSION)
        self.assertIn("Vector3.Dot(forward, delta) > 0.84f * Mathf.Sqrt(distanceSqr)", SHOT_ADMISSION)

    def test_virtual_shot_uses_actual_post_motion_position(self):
        self.assertLess(VIRTUAL.index("selfPosition = next;"),
                        VIRTUAL.index("HorizontalShotDistanceSquared(selfPosition, targetPosition)"))
        self.assertLess(VIRTUAL.index("HorizontalShotDistanceSquared(selfPosition, targetPosition)"),
                        VIRTUAL.index("owner.IsVirtualShotBlocked(selfPosition, targetPosition)"))
        self.assertIn("float shotReach = FireRange;", VIRTUAL)
        self.assertLess(VIRTUAL.index("float shotReach = FireRange;"),
                        VIRTUAL.index("shotDistanceSqr > shotReach * shotReach"))
        self.assertIn("shotDistanceSqr > shotReach * shotReach", VIRTUAL)
        self.assertIn("Mathf.Sqrt(shotDistanceSqr) / Mathf.Max(1f, shotReach)", VIRTUAL)
        post_move_shot = VIRTUAL.split("float shotDistanceSqr =", 1)[1]
        self.assertNotRegex(post_move_shot, r"\bdistance(?:Sqr)?\s*(?:<=|>=|<|>)")
        self.assertIn("!hasTarget || now < nextAttack || (carriedGun != null && !settledForShot)", VIRTUAL)
        for unchanged in ("targetHealth.ApplyDamage(damage, gameObject);",
                          "if (!cachedVirtualDamageRival.virtualBody)",
                          "(targetPosition - playerPosition).sqrMagnitude",
                          "owner.IsVirtualShotBlocked(selfPosition, targetPosition)"):
            self.assertIn(unchanged, VIRTUAL)
        # Range, near-player exclusion, physical-victim admission and cover
        # rejection must all run before spending ammunition or applying damage.
        gates = [VIRTUAL.index(token) for token in (
            "shotDistanceSqr > shotReach * shotReach",
            "(targetPosition - playerPosition).sqrMagnitude",
            "if (!cachedVirtualDamageRival.virtualBody)",
            "owner.IsVirtualShotBlocked(selfPosition, targetPosition)",
            "if (carriedGun != null) carriedMagazine--;",
            "targetHealth.ApplyDamage(damage, gameObject);")]
        self.assertEqual(gates, sorted(gates))

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
