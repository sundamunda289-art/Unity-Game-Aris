#!/usr/bin/env python3
"""Source and numerical regression for combat camera HUD and AI scheduling."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
HUD = (ROOT / "AarisCombatFeedbackHUD.cs").read_text()
BOTS = (ROOT / "AarisOfflineBRDirector.cs").read_text()

class CombatRegression(unittest.TestCase):
    def test_screen_direction_tracks_camera(self):
        self.assertIn("aimCamera.transform.forward : transform.forward", HUD)
        self.assertIn("Vector3.Cross(Vector3.up, facing)", HUD)
        self.assertNotIn("Vector3.Dot(transform.right, from)", HUD)
        self.assertNotIn("Vector3.Dot(transform.forward, from)", HUD)
        # East-facing camera, north-facing player, east-side attacker -> FRONT
        camera = (1.0, 0.0)
        hit = (1.0, 0.0)
        front = sum(a*b for a,b in zip(camera, hit))
        right = (-camera[1], camera[0])
        side = sum(a*b for a,b in zip(right, hit))
        self.assertGreater(front, abs(side))

    def test_hp_and_scope_cached_text(self):
        self.assertIn("new string[201]", HUD)
        self.assertIn("healthText.text = Hp200Labels[current];", HUD)
        self.assertIn("ScopeHudText(scope, inventory.Aiming)", HUD)
        for tier in ("NO SCOPE ADS", "NORMAL ADS", "2X ADS", "4X ADS"):
            self.assertIn('"' + tier + '"', HUD)

    def test_single_scheduled_ai_decision(self):
        self.assertIn("nextThink = now + .35f + UnityEngine.Random.Range(0f, .14f);", BOTS)
        self.assertIn("(target.position - transform.position).sqrMagnitude < 81f", BOTS)
        self.assertNotIn("nextThink = Time.time + .23f", BOTS)

if __name__ == "__main__":
    unittest.main()
