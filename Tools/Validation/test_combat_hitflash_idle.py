#!/usr/bin/env python3
"""Static regression for idle UI overdraw and the incoming HP damage flash.

This validates source wiring; it is not a substitute for Unity PlayMode or
real-device GPU profiling.
"""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisCombatFeedbackHUD.cs").read_text()


class CombatFlashLifecycle(unittest.TestCase):
    def test_overlay_starts_inactive(self):
        build = SOURCE.split("private void Build()", 1)[1].split(
            "private void HealthChanged(", 1)[0]
        self.assertIn("hitFlash.gameObject.SetActive(false);", build)
        self.assertIn("new Color(1f, .03f, .03f, 0f)", build)
        self.assertIn('new GameObject("Combat feedback"', build)

    def test_real_hp_loss_reactivates_flash(self):
        damage = SOURCE.split("private void Damaged(", 1)[1].split(
            "public static void NotifyOutgoingDamage(", 1)[0]
        self.assertIn("if (receiver != health) return;", damage)
        self.assertIn("hitFadeUntil = Time.unscaledTime + .35f;", damage)
        self.assertIn("if (hitFlash != null && !hitFlash.gameObject.activeSelf)", damage)
        self.assertIn("hitFlash.gameObject.SetActive(true);", damage)

    def test_no_fullscreen_color_updates_while_inactive(self):
        tick = SOURCE.split("private void LateUpdate()", 1)[1].split(
            "private void OnDestroy()", 1)[0]
        gated = tick.index("if (hitFlash != null && hitFlash.gameObject.activeSelf)")
        inactive = tick.index("hitFlash.gameObject.SetActive(false);")
        color = tick.index("hitFlash.color = new Color(")
        self.assertLess(gated, inactive)
        self.assertLess(inactive, color)
        self.assertIn("float remaining = hitFadeUntil - now;", tick)
        self.assertIn("if (remaining <= 0f)", tick)
        self.assertIn("Mathf.Clamp01(remaining / .35f) * .2f", tick)

    def test_incoming_direction_expiry_is_independent(self):
        tick = SOURCE.split("private void LateUpdate()", 1)[1].split(
            "for (int i = 0; i < numbers.Length; i++)", 1)[0]
        self.assertIn("if (hitDirection != null && hitDirection.gameObject.activeSelf &&",
                      tick)
        self.assertIn("now >= hitFadeUntil) hitDirection.gameObject.SetActive(false);",
                      tick)
        self.assertLess(tick.index("hitFlash.gameObject.SetActive(false);"),
                        tick.index("hitDirection.gameObject.SetActive(false);"))


if __name__ == "__main__":
    unittest.main()
