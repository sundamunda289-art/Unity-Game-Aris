#!/usr/bin/env python3
"""Regression guards for time-bounded rival-on-rival retaliation.

These source-contract checks supplement native Unity compilation and authored-scene
PlayMode. They do not claim to simulate the Unity physics engine or phone FPS.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text(
    encoding="utf-8")
RIVAL = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
THINK = RIVAL.split("if (now >= nextThink)", 1)[1].split(
    "// Only target changes refresh this reference", 1)[0]
DAMAGE = RIVAL.split("private void OnDamaged(", 1)[1].split(
    "internal void TakeZoneDamage(", 1)[0]


class RivalRetaliationTests(unittest.TestCase):
    def test_player_slots_keep_priority_over_rival_retaliation(self):
        self.assertLess(THINK.index("owner.MayAttackPlayer(this)"),
                        THINK.index("recentRivalAttacker != null"))
        self.assertIn("target = playerTarget;", THINK)
        self.assertIn("target = recentRivalAttacker.transform;", THINK)
        self.assertIn("owner.MayAttackPlayer(this)", RIVAL.split(
            "private void UpdateAnimationBlend", 1)[0])
        self.assertNotIn("target = playerTarget;", THINK.split(
            "recentRivalAttacker != null", 1)[1])

    def test_only_a_living_rival_with_actual_hp_loss_is_remembered(self):
        self.assertIn("loss <= 0f", DAMAGE)
        self.assertIn("attacker.TryGetComponent<AarisOfflineRival>", DAMAGE)
        self.assertIn("rivalAttacker != this && rivalAttacker.IsAlive", DAMAGE)
        self.assertIn("recentRivalAttacker = rivalAttacker;", DAMAGE)
        self.assertNotIn("recentRivalAttacker = playerTarget", RIVAL)

    def test_memory_expires_and_caps_hunt_range(self):
        self.assertIn("recentRivalAttackerUntil = Time.time + 4f;", DAMAGE)
        self.assertIn("now < recentRivalAttackerUntil", THINK)
        self.assertIn("110f * 110f", THINK)
        self.assertIn("recentRivalAttacker.IsAlive", THINK)
        self.assertIn("owner.FindRivalOpponent(this, out target, out targetHealth)",
                      THINK)
        for elapsed, distance, alive, expected in (
            (0.01, 40, True, True), (3.99, 109, True, True),
            (4.0, 40, True, False), (2, 111, True, False),
            (1, 40, False, False),
        ):
            self.assertEqual(elapsed < 4.0 and distance < 110 and alive,
                             expected)

    def test_reactive_target_still_needs_real_line_of_sight(self):
        update = RIVAL.split("private void Update()", 1)[1].split(
            "private void UpdateAnimationBlend", 1)[0]
        self.assertIn("canSee = target != null && targetHealth != null", update)
        self.assertIn("HasSight();", update)
        self.assertIn("TryGetSightHit(target, out RaycastHit shotHit)", update)
        self.assertIn("MuzzlePathClear(target)", update)
        self.assertIn("owner.TryFindVisibleRivalAlternative", update)

    def test_original_cover_reaction_and_2_attacker_director_preserved(self):
        self.assertIn("nextHitReaction = Time.time + 0.22f;", DAMAGE)
        self.assertIn("nextCoverQuery = Time.time + 1.5f;", DAMAGE)
        self.assertIn("Physics.OverlapSphereNonAlloc", DAMAGE)
        director = SOURCE.split("internal sealed class AarisOfflineRival", 1)[0]
        self.assertIn("private const int AttackerSelectionPerFrame = 12;", director)
        self.assertIn("return bot == attackerOne || bot == attackerTwo;", director)


if __name__ == "__main__":
    unittest.main()
