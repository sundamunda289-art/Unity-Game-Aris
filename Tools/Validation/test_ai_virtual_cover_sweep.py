#!/usr/bin/env python3
"""Source and arithmetic tests of distant-bot cover sweep; not Unity PhysX."""
from pathlib import Path
import math
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()

def blocked(distance, wall_start, wall_end):
    half = 0.36
    endpoint = max(distance, 0.72)
    if endpoint + half >= wall_start and endpoint - half <= wall_end:
        return True
    if distance <= 0.72:
        return False
    steps = math.ceil(distance / 0.36)
    if steps > 17:
        return True
    for i in range(1, steps):
        x = distance * i / steps
        if x + half >= wall_start and x - half <= wall_end:
            return True
    return False

class BotInactiveCoverSweep(unittest.TestCase):
    def test_production_sweep_is_present(self):
        for fragment in (
            "if (distance <= 0.72f) return false;",
            "int steps = Mathf.CeilToInt(distance / stride);",
            "if (steps > 17) return true;",
            "for (int step = 1; step < steps; step++)",
            "owner.WouldCrossInactiveCover(selfPosition,",
        ):
            self.assertIn(fragment, SOURCE)

    def test_thin_cover_cannot_be_tunneled(self):
        self.assertTrue(blocked(4.0, 1.8, 2.0))
        self.assertTrue(blocked(1.12, .49, .57))

    def test_clear_path_and_short_step(self):
        self.assertFalse(blocked(4.0, 6.0, 6.2))
        self.assertFalse(blocked(.35, 1.7, 1.8))
        self.assertTrue(blocked(.35, .6, .8))

    def test_giant_unverified_step_fails_closed(self):
        self.assertTrue(blocked(10.0, 100.0, 101.0))

if __name__ == "__main__":
    unittest.main()
