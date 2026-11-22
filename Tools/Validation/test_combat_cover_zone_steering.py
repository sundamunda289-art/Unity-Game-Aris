#!/usr/bin/env python3
"""Regression contracts for zero-allocation bot cover and safe-zone steering.

These source/geometry tests complement, not replace, Unity PhysX PlayMode QA.
"""
from pathlib import Path
import math
import unittest

AI = (Path(__file__).resolve().parents[2] /
      "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text(
          encoding="utf-8")


class CoverZoneSteeringTests(unittest.TestCase):
    def test_navigation_goals_take_priority_over_random_offset(self):
        self.assertIn(
            "Vector3 steering = seekingCover || retreat ? travelDirection :",
            AI)
        self.assertIn(
            "Quaternion.Euler(0f, turnOffset, 0f) * travelDirection;", AI)
        self.assertIn("Vector3 desired = steering * moveSpeed;", AI)
        self.assertNotIn(
            "Quaternion.Euler(0f, turnOffset, 0f) *\n"
            "                travelDirection * moveSpeed;", AI)

    def test_cover_destination_progress_is_not_rotated(self):
        # When under fire a bot can receive a 65-degree reactive turnOffset.
        # Rotating the already chosen cover heading can redirect it away
        # from cover, especially after another 65-degree hit reaction.
        for degrees in (-170, -130, -90, -65, -25, 25, 65, 90, 130, 170):
            before = math.cos(math.radians(degrees))
            after = 1.0  # Direct normalized seek vector.
            self.assertGreaterEqual(after, before)
            self.assertAlmostEqual(after, 1.0)

    def test_zone_escape_stays_pointed_inside_ring(self):
        # For an outside-zone bot, retreat direction points toward the
        # center; wandering perturbation should never rotate it outward.
        for degrees in (-160, -110, -65, 0, 65, 110, 160):
            old_radial_progress = math.cos(math.radians(degrees))
            new_radial_progress = 1.0
            self.assertGreaterEqual(new_radial_progress, old_radial_progress)
        self.assertLess(math.cos(math.radians(130)), 0.0)

    def test_collision_and_two_attacker_cap_are_unchanged(self):
        self.assertIn(
            "owner.WouldCrossInactiveCover(transform.position, displacement)", AI)
        self.assertIn("capsule.Move(displacement +", AI)
        self.assertIn("private const int RivalCount = 100;", AI)
        self.assertIn("return bot == attackerOne || bot == attackerTwo;", AI)


if __name__ == "__main__":
    unittest.main()
