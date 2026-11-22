#!/usr/bin/env python3
"""Combat regression: AI can reload under cover without a visible target.

Source contracts supplement Unity PlayMode integration; they are not a
substitute for a real native Unity compile or Android frame-time capture.
"""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text(
              encoding="utf-8")
RIVAL_UPDATE = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
RIVAL_UPDATE = RIVAL_UPDATE.split("private void Update()", 1)[1]
RIVAL_UPDATE = RIVAL_UPDATE.split("private void UpdateAnimationBlend", 1)[0]
# Physical and virtual opponents deliberately have independent reload paths.
PHYSICAL_UPDATE, VIRTUAL_UPDATE = RIVAL_UPDATE.split(
    "// Distant bots stay fully alive as logical actors", 1)



class BotCoverReloadTests(unittest.TestCase):
    def test_reload_is_not_inside_visible_target_attack_gate(self):
        reload_gate = ("if (carriedGun != null && carriedMagazine == 0 "
                       "&& now >= nextAttack)")
        combat_gate = "if (hasTarget && canSee &&"
        self.assertEqual(PHYSICAL_UPDATE.count(reload_gate), 1)
        self.assertEqual(VIRTUAL_UPDATE.count(reload_gate), 1)
        self.assertLess(PHYSICAL_UPDATE.index(reload_gate),
                        PHYSICAL_UPDATE.index(combat_gate))
        self.assertNotIn("if (carriedGun != null && carriedMagazine == 0)",
                         PHYSICAL_UPDATE[PHYSICAL_UPDATE.index(combat_gate):])
        # Virtual enemies may reload while fighting each other offscreen.
        self.assertLess(VIRTUAL_UPDATE.index(reload_gate),
                        VIRTUAL_UPDATE.index("targetHealth.ApplyDamage"))

    def test_reload_cannot_grant_infinite_bullets(self):
        self.assertIn("int refill = Mathf.Min(carriedGun.magazineSize, "
                      "carriedReserve);", RIVAL_UPDATE)
        self.assertIn("carriedMagazine = refill;", RIVAL_UPDATE)
        self.assertIn("carriedReserve -= refill;", RIVAL_UPDATE)
        self.assertIn("nextAttack = now + carriedGun.reloadSeconds;",
                      RIVAL_UPDATE)
        self.assertIn("carriedMagazine--;", RIVAL_UPDATE)
        self.assertIn("if (carriedReserve <= 0)", RIVAL_UPDATE)
        self.assertIn("carriedGun = null;", RIVAL_UPDATE)

    def test_reload_does_not_bypass_muzzle_and_two_attacker_gates(self):
        self.assertIn("MuzzlePathClear(target)", RIVAL_UPDATE)
        self.assertIn("owner.MayAttackPlayer(this)", RIVAL_UPDATE)
        self.assertIn("AarisEquippedGun.ApplyDamage(", RIVAL_UPDATE)
        self.assertIn("if (canSee && !retreat && !seekingCover",
                      RIVAL_UPDATE)

    def test_ammo_math_under_cover_matches_finite_magazine_contract(self):
        # Simulated empty-gun transitions, independent of target visibility.
        for capacity, reserve in ((30, 120), (5, 2), (6, 0), (36, 1)):
            magazine = 0
            weapon = capacity
            next_attack = 4.0
            now = 4.0
            if weapon is not None and magazine == 0 and now >= next_attack:
                if reserve <= 0:
                    weapon = None
                    next_attack = now + 0.7
                else:
                    refill = min(weapon, reserve)
                    magazine = refill
                    reserve -= refill
                    next_attack = now + 2.5
            if capacity == 6:
                self.assertIsNone(weapon)
                self.assertEqual(magazine, 0)
            else:
                self.assertEqual(magazine, min(capacity,
                                              {30: 120, 5: 2, 36: 1}[capacity]))
                self.assertGreater(next_attack, now)
                self.assertGreaterEqual(reserve, 0)


if __name__ == "__main__":
    unittest.main()
