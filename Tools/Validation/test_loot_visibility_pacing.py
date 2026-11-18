#!/usr/bin/env python3
"""Deterministic source/pacing guard for streamed off-screen loot visuals.

This is a Python model of the timing contract, not Unity rendering or GPU FPS.
"""
from pathlib import Path
import unittest

SRC = (Path(__file__).resolve().parents[2] /
       "Assets/AarisWorld/Scripts/AarisWeaponPickup.cs").read_text()


def simulate(fps, appearing_at=2.0, duration=4.0):
    pending = applied = 0.0
    next_check = 0.0
    was_visible = False
    hidden_queries = visible_queries = 0
    first_visible_poll = None
    visible_frames = 0
    for frame in range(int(round(duration * fps))):
        now = frame / fps
        pending = (pending + 19.0 / fps) % 360.0
        if not was_visible and now < next_check:
            continue
        if now < appearing_at:
            hidden_queries += 1
            was_visible = False
            next_check = now + 0.10
            continue
        visible_queries += 1
        visible_frames += 1
        if first_visible_poll is None:
            first_visible_poll = now
        was_visible = True
        applied = (applied + pending) % 360.0
        pending = 0.0
    return hidden_queries, visible_queries, visible_frames, first_visible_poll, applied


class LootVisibilityPacing(unittest.TestCase):
    def test_real_render_path_unchanged(self):
        update = SRC.split("private void Update()", 1)[1].split(
            "private void OnEnable()", 1
        )[0]
        assert "pendingSpinDegrees += 19f * Time.deltaTime;" in update
        assert "Mathf.Repeat(pendingSpinDegrees, 360f)" in update
        assert "if (!modelWasVisible && Time.unscaledTime < nextModelVisibilityCheck)" in update
        assert "nextModelVisibilityCheck = Time.unscaledTime + 0.10f;" in update
        assert update.index("if (renderer == null || !renderer.isVisible)") < update.index(
            "modelWasVisible = visibleNow;") < update.index("model.Rotate(")
        assert update.index("model.Rotate(") < update.index("pendingSpinDegrees = 0f;")
        on_enable = SRC.split("private void OnEnable()", 1)[1]
        assert "modelWasVisible = false;" in on_enable
        assert "nextModelVisibilityCheck = 0f;" in on_enable
        assert "private const int MaximumActiveLoot = 40;" in (
            Path(__file__).resolve().parents[2] /
            "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs"
        ).read_text()

    def test_hidden_poll_budget_and_visible_fidelity(self):
        for fps in (15, 30, 60, 90, 120):
            with self.subTest(fps=fps):
                hidden, visible, frames, first, angle = simulate(fps)
                self.assertLessEqual(hidden, 21)
                self.assertGreater(hidden, 10)
                self.assertEqual(visible, frames)
                self.assertIsNotNone(first)
                self.assertLessEqual(first - 2.0, 0.10 + 1.0 / fps)
                self.assertAlmostEqual(angle, 19.0 * 4.0, places=6)

    def test_continuous_visible_model_still_uses_every_frame(self):
        for fps in (15, 30, 60, 120):
            hidden, visible, frames, first, angle = simulate(fps, 0.0)
            self.assertEqual(hidden, 0)
            self.assertEqual(visible, int(round(4.0 * fps)))
            self.assertEqual(frames, visible)
            self.assertEqual(first, 0.0)
            self.assertAlmostEqual(angle, 76.0, places=6)


if __name__ == "__main__":
    unittest.main(verbosity=2)
