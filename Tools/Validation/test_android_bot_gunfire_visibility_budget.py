"""Source contracts for optional, camera-invisible bot muzzle/tracer culling.

No damage, ammo, physics, kill-feed or AI tick paths are changed by this VFX gate.
"""
from pathlib import Path
import unittest

SRC = (Path(__file__).resolve().parents[2] /
       "Assets/AarisWorld/Scripts/AarisRivalGunfireVfx.cs").read_text()


class OffscreenBotGunfireBudget(unittest.TestCase):
    def test_cull_is_conservative_and_camera_safe(self):
        self.assertIn("private bool ShotCompletelyOffscreen(Vector3 from, Vector3 to)", SRC)
        self.assertIn("camera == null || !camera.isActiveAndEnabled", SRC)
        self.assertIn("a.z <= 0f || b.z <= 0f) return false", SRC)
        self.assertIn("camera.WorldToViewportPoint(from)", SRC)
        self.assertIn("camera.WorldToViewportPoint(to)", SRC)

    def test_no_visual_pool_work_for_completely_invisible_shots(self):
        emit = SRC.split("private void EmitWithBarrel(", 1)[1].split(
            "internal void EmitForGun(", 1)[0]
        self.assertLess(emit.index("ShotCompletelyOffscreen(from, target)"),
                        emit.index("nextSlot++ % MaximumSlots"))
        self.assertLess(emit.index("ShotCompletelyOffscreen(from, target)"),
                        emit.index("CreateCasingSlot()"))
        self.assertIn("enabled = true;", emit)

    def test_still_has_shared_pool_and_idle_sleep(self):
        self.assertIn("private const int MaximumSlots = 6;", SRC)
        self.assertIn("private const int MaximumCasingSlots = 12;", SRC)
        self.assertIn("if (!anyLiveEffect) enabled = false;", SRC)


if __name__ == "__main__":
    unittest.main()
