#!/usr/bin/env python3
"""Regression: real rival punches are physical body hits, not gun headshots.

This validates production source wiring. Unity PhysX collision and HP
damage are exercised by AarisFullSceneCombatSoak native Play Mode QA.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
RIVAL = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
GUN = (ROOT / "Assets/AarisWorld/Scripts/AarisEquippedGun.cs").read_text()


class RivalMeleeDamageRoute(unittest.TestCase):
    def test_rival_bullet_preserves_actual_physics_hit_coordinate(self):
        attack = RIVAL.split("// Keep the same physical LOS for guns and punches.", 1)[1]
        attack = attack.split("private void UpdateAnimationBlend(", 1)[0]
        self.assertRegex(
            attack,
            r"if \(carriedGun != null\)\s+AarisEquippedGun\.ApplyCombatDamage"
            r"\(shotHit, damage,\s+gameObject, carriedGun\.headshotMultiplier\);",
        )
        # The unified receiver keeps the actual physical hit point; logical
        # receivers use the same explicit hit data without fabricated colliders.
        route = GUN.split("internal static bool ApplyCombatDamage(", 1)[1].split(
            "internal static bool ApplyDamage(", 1)[0]
        self.assertIn("ApplyDamage(hit.collider, amount, attacker, hit.point, headshotMultiplier)", route)

    def test_rival_unarmed_punch_uses_body_only_damage_route(self):
        attack = RIVAL.split("// Keep the same physical LOS for guns and punches.", 1)[1]
        attack = attack.split("private void UpdateAnimationBlend(", 1)[0]
        self.assertRegex(
            attack,
            r"else\s+AarisEquippedGun\.ApplyCombatDamage"
            r"\(shotHit, damage,\s+gameObject, 1f\);",
        )
        # The LOS/muzzle guards are in the conditional immediately ABOVE
        # the damage branch; keep their production authority unchanged.
        self.assertIn("TryGetCombatSightHit(target, out AarisCombatHit shotHit)", RIVAL)
        self.assertIn("MuzzlePathClear(target)", RIVAL)
        route = GUN.split("internal static bool ApplyCombatDamage(", 1)[1].split(
            "internal static bool ApplyDamage(", 1)[0]
        self.assertIn("headshotMultiplier <= 1f", route)
        self.assertIn("? ApplyDamage(hit.collider, amount, attacker)", route)
        self.assertIn("height * .79f && headshotMultiplier > 1f", route)

    def test_unpositioned_damage_cannot_create_headshot(self):
        self.assertIn("if (hitPosition.HasValue)", GUN)
        self.assertIn("if (headshot) damage *= Mathf.Clamp(headshotMultiplier, 1f, 4f);", GUN)
        self.assertIn("hitPosition ?? collider.bounds.center", GUN)
        self.assertIn("actualLoss > 0f", GUN)


if __name__ == "__main__":
    unittest.main()
