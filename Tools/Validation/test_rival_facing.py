#!/usr/bin/env python3
"""Rival locomotion-facing regression (source/simulation only, not Play Mode)."""
from pathlib import Path
import math
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text("utf-8")
RIVAL = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
UPDATE = RIVAL.split("private void Update()", 1)[1].split(
    "private bool HasSight()", 1)[0]


def facing_2d(toward, displacement, can_see, retreat):
    dx, dz = displacement
    if (not can_see or retreat) and dx*dx + dz*dz > .000001:
        length = math.hypot(dx, dz)
        return (dx / length, dz / length)
    return toward


class RivalFacingRegression(unittest.TestCase):
    def test_source_uses_post_avoidance_movement(self):
        facing = UPDATE.split(
            "// Follow the direction of actual planned movement", 1)[1].split(
                "capsule.Move(", 1)[0]
        self.assertIn(
            "if ((!canSee || retreat) && displacement.sqrMagnitude > 0.000001f)",
            facing)
        self.assertIn("facing = displacement.normalized;", facing)
        self.assertLess(UPDATE.index("owner.WouldCrossInactiveCover"),
                        UPDATE.index("facing = displacement.normalized;"))
        self.assertLess(UPDATE.index("facing = displacement.normalized;"),
                        UPDATE.index("capsule.Move("))
        self.assertIn(
            "Vector3 facing = distance > .05f ? toward : selfForward;", facing)
        self.assertNotIn("Physics.", facing)
        self.assertNotIn("GetComponent", facing)

    def test_visible_engagement_preserves_target_facing(self):
        self.assertEqual(facing_2d((0, 1), (1, 0), True, False), (0, 1))

    def test_zone_retreat_faces_path_even_with_target_visible(self):
        self.assertEqual(facing_2d((0, 1), (1, 0), True, True), (1, 0))

    def test_occluded_house_avoidance_follows_corridor(self):
        self.assertEqual(facing_2d((0, 1), (-3, 0), False, False), (-1, 0))

    def test_blocked_or_tiny_displacement_keeps_stable_facing(self):
        self.assertEqual(facing_2d((0, 1), (0, 0), False, True), (0, 1))
        self.assertEqual(facing_2d((0, 1), (0.0001, 0), False, False), (0, 1))

    def test_source_preserves_combat_and_controller(self):
        self.assertIn("if (hasTarget && canSee &&", UPDATE)
        self.assertIn("IsPostMoveShotInRangeAndFacing(afterMove, transform.forward,", UPDATE)
        self.assertIn("owner.MayAttackPlayer(this)", UPDATE)
        self.assertIn("CollisionFlags hit = capsule.Move(displacement +", UPDATE)
        self.assertIn("float wanted = Mathf.Clamp01(groundSpeed / 2.9f);", UPDATE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
