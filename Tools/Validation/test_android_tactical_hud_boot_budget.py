#!/usr/bin/env python3
"""Android aircraft-first tactical HUD construction: source regression checks."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
TACTICAL = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineTacticalHUD.cs").read_text()
DROP = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineDrop.cs").read_text()
WEAPON = (ROOT / "Assets/AarisWorld/Scripts/AarisWeaponHUD.cs").read_text()


class AndroidFlightTacticalHud(unittest.TestCase):
    def test_android_flight_only_defer_and_editor_immediate(self):
        start = TACTICAL.split("private void Start()", 1)[1].split(
            "private IEnumerator BuildAfterAndroidFlightFrames()", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", start)
        self.assertIn("if (drop != null && !drop.HasLanded)", start)
        self.assertIn("StartCoroutine(BuildAfterAndroidFlightFrames());", start)
        self.assertIn("bool deferTacticalHud = false;", start)
        self.assertIn("deferTacticalHud = true;", start)
        self.assertIn("if (!deferTacticalHud)", start)
        self.assertIn("Build();", start)
        self.assertIn("initialized = true;", start)
        self.assertNotIn("else\\n#endif", start)
        self.assertLess(start.index("StartCoroutine(BuildAfterAndroidFlightFrames());"),
                        start.index("if (!deferTacticalHud)"))
        self.assertIn("if (motor == null || gloo == null) { enabled = false; return; }", start)

    def test_at_least_two_frames_before_optional_build(self):
        method = TACTICAL.split("private IEnumerator BuildAfterAndroidFlightFrames()", 1)[1].split(
            "// Stripped player prefabs", 1)[0]
        self.assertEqual(method.count("yield return null;"), 2)
        self.assertLess(method.index("RefreshOptionalComponents(true);"), method.index("Build();"))
        self.assertLess(method.index("Build();"), method.index("initialized = true;"))
        self.assertIn("if (!isActiveAndEnabled) yield break;", method)

    def test_aircraft_controls_not_blocked_by_new_overlay(self):
        method = TACTICAL.split("private IEnumerator BuildAfterAndroidFlightFrames()", 1)[1].split(
            "// Stripped player prefabs", 1)[0]
        self.assertIn("bool onFoot = (drop == null || drop.HasLanded)", method)
        self.assertIn("(vehicle == null || !vehicle.IsDriving)", method)
        self.assertIn("canvas.enabled = false;", method)
        self.assertIn("raycaster.enabled = false;", method)
        self.assertLess(method.index("raycaster.enabled = false;"), method.index("initialized = true;"))
        self.assertIn("if (!initialized) return;", TACTICAL)

    def test_existing_tactical_controls_and_prewarm_retained(self):
        for token in ('CreateAction("Gloo Wall"', 'CreateAction("Frag Grenade"',
                      'CreateAction("Medkit"', 'CreateAction("Helmet durability"',
                      "StartCoroutine(AarisOfflineFragGrenade.PrewarmGrenadeAssets());"):
            self.assertIn(token, TACTICAL)
        self.assertIn("private void OnDisable() => CancelTacticalInput();", TACTICAL)

    def test_weapon_hud_cached_by_flight_unchanged(self):
        self.assertIn("weaponCanvas = weaponHud != null ? weaponHud.GetComponentInChildren<Canvas>(true) : null;", DROP)
        self.assertIn("ui.Create(inv);", WEAPON)
        self.assertNotIn("BuildAfterAndroidFlightFrames", WEAPON)


if __name__ == "__main__":
    unittest.main(verbosity=2)
