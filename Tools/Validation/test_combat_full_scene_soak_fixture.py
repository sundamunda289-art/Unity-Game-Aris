"""Regression guards for the authored 100-rival Unity scene acceptance fixture."""
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Editor/AarisFullSceneCombatSoak.cs")


class FullSceneCombatSoakFixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.code = SOURCE.read_text(encoding="utf-8")

    def test_existing_real_rival_reacquired_after_streaming(self):
        self.assertIn("private static Component FindRealScopeVictim()", self.code)
        self.assertIn("if (peakVisibleBodies == 0) return null;", self.code)
        self.assertIn('GetMethod("BuildDistantVisual", Private)', self.code)
        self.assertIn("QA_SCOPE_REACQUIRED_LIVE_RIVAL=production_visual_after_streaming",
                      self.code)
        self.assertIn("if (!HasVisibleBody(candidate)) continue;", self.code)

    def test_natural_damage_snapshot_precedes_scripted_hud_hit(self):
        sampled = self.code.index("int naturalDamageEvents = damageEvents;")
        scripted = self.code.index("VerifyScopedCombatHudOnRealActors();")
        self.assertLess(sampled, scripted)
        self.assertIn("NATURAL_BOT_TO_PLAYER_DAMAGE_EVENTS=", self.code)

    def test_random_no_contact_soak_does_not_forge_bot_hit(self):
        self.assertNotIn("damageEvents > 0 && !hadRuntimeError", self.code)
        self.assertIn("peakSpawned == 100", self.code)
        self.assertIn("(visible > 0 || peakVisibleBodies > 0)", self.code)
        self.assertIn("PASS sustained 100-bot combat simulation", self.code)


if __name__ == "__main__":
    unittest.main()
