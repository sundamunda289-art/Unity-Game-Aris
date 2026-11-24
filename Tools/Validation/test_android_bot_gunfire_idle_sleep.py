#!/usr/bin/env python3
"""Contracts for the event-driven shared bot gunfire VFX driver."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
VFX = (ROOT / "Assets/AarisWorld/Scripts/AarisRivalGunfireVfx.cs").read_text()
AI = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
EMIT = VFX.split("private void EmitWithBarrel(", 1)[1].split(
    "internal void EmitForGun(", 1)[0]
UPDATE = VFX.split("private void Update()", 1)[1]


class IdleBotGunfireTests(unittest.TestCase):
    def test_idle_component_has_no_update_dispatch_until_real_physical_shot(self):
        self.assertIn("private void Awake() { enabled = false; }", VFX)
        self.assertIn("enabled = true;", EMIT)
        self.assertLess(EMIT.index("if (observer == null || shooter == null"),
                        EMIT.index("enabled = true;"))
        self.assertIn("if (!anyLiveEffect) enabled = false;", UPDATE)

    def test_active_tracers_lights_grounded_and_delayed_casings_keep_driver_awake(self):
        self.assertIn("slot.flash.enabled || slot.tracer.enabled || slot.burst.enabled", UPDATE)
        self.assertIn("if (casing != null && casing.queued) anyLiveEffect = true;", UPDATE)
        self.assertIn("anyLiveEffect = true; // Keep grounded casings", UPDATE)
        self.assertLess(UPDATE.index("if (now >= casing.until)"),
                        UPDATE.index("anyLiveEffect = true; // Keep grounded casings"))
        self.assertIn("if (gun.category == \"Pistol\")", VFX)
        self.assertIn("if (delay <= 0f) return;", VFX)
        self.assertIn("casing.queued = true;", VFX)

    def test_never_create_per_bot_renderers_or_simulate_remote_damage(self):
        self.assertIn("private const int MaximumSlots = 6;", VFX)
        self.assertIn("private const int MaximumCasingSlots = 12;", VFX)
        self.assertNotIn("Instantiate(", UPDATE)
        self.assertNotIn("new GameObject(", UPDATE)
        self.assertNotIn("new Slot(", UPDATE)
        self.assertNotIn("new CasingSlot(", UPDATE)
        self.assertIn("TickVirtualCombat(now, dt, playerPosition);", AI)
        self.assertIn("targetHealth.ApplyDamage(damage, gameObject);", AI)

    def test_pump_and_bolt_remain_live_through_ejection_at_30_to_120_fps(self):
        for fps in (30, 60, 120):
            for delay in (0.0, .29, .31):
                running = True  # Emit wakes on accepted physical shot.
                shot_at = 0.
                tracer_until = .21
                flash_until = .045
                eject_at = delay
                casing_until = .85 + delay
                queued = delay > 0
                renderer_enabled = not queued
                eject_count = 0
                for frame in range(1, int((casing_until + .3) * fps) + 1):
                    now = frame / fps
                    if not running:
                        continue
                    flash = now < flash_until
                    tracer = now < tracer_until
                    if queued and now >= eject_at:
                        queued = False
                        renderer_enabled = True
                        eject_count += 1
                    if renderer_enabled and now >= casing_until:
                        renderer_enabled = False
                    running = flash or tracer or queued or renderer_enabled
                self.assertFalse(running)
                self.assertEqual(eject_count, 1 if delay else 0)


if __name__ == "__main__":
    unittest.main()
