#!/usr/bin/env python3
"""Rig lifecycle fixture isolates one Editor actor from stochastic PvP death."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
FILE = (ROOT / "Assets/AarisWorld/Editor/AarisFullSceneCombatSoak.cs").read_text()
HP = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineHealth.cs").read_text()
START = FILE.split("private static void PrepareRigRetentionCheck()", 1)[1].split(
    "private static void VerifyRigRetirement()", 1)[0]

class EditorRigFixture(unittest.TestCase):
    def test_fixture_shield_only_after_real_combat_stage(self):
        self.assertIn("if (LoadedSecondsMinimum > 5f)", FILE)
        self.assertIn("PASS sustained 100-bot combat", FILE)
        self.assertIn("AarisOfflineHealth fixtureHealth =", START)
        self.assertIn('GetField("maximumHealth", Private)', START)
        self.assertIn('GetField("current", Private)', START)
        self.assertIn("const float fixtureHealthReserve = 100000f;", START)
        self.assertIn("fixtureMaximum.SetValue(fixtureHealth, fixtureHealthReserve)", START)
        self.assertIn("fixtureCurrent.SetValue(fixtureHealth, fixtureHealthReserve)", START)
        self.assertIn("RIG_LIFECYCLE_FIXTURE_PVP_DAMAGE_ISOLATED=1", START)
        self.assertLess(START.index("retained.enabled = false;"),
                        START.index("fixtureCurrent.SetValue"))
        self.assertLess(START.index("fixtureCurrent.SetValue"),
                        START.index("stage = 4;"))
        self.assertNotIn("fixtureHealthReserve", HP)

if __name__ == "__main__":
    unittest.main()
