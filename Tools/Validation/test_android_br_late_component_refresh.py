#!/usr/bin/env python3
"""Guard the low-allocation BR startup lookup without changing drop gameplay.

Static source assertions only; Unity Editor and device profiling are separate.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()


class TestBrLateComponentRefresh(unittest.TestCase):
    def test_missing_drop_lookup_is_paced(self):
        update = SOURCE.split("private void Update()", 1)[1].split(
            "private bool CanAttemptRivalSpawn()", 1
        )[0]
        self.assertIn("if (drop == null && Time.unscaledTime >= nextDropComponentRefresh)", update)
        self.assertIn("nextDropComponentRefresh = Time.unscaledTime + 1f;", update)
        self.assertEqual(update.count("player.GetComponent<AarisOfflineDrop>()"), 1)

    def test_gameplay_gate_and_visuals_unchanged(self):
        update = SOURCE.split("private void Update()", 1)[1].split(
            "private bool CanAttemptRivalSpawn()", 1
        )[0]
        self.assertLess(update.index("drop = player.GetComponent<AarisOfflineDrop>();"),
                        update.index("if (drop != null && !drop.HasLanded) return;"))
        self.assertLess(update.index("if (!rivalVisualPrewarmComplete) return;"),
                        update.index("Begin();"))
        self.assertIn("if (Time.time >= nextZoneTick)", update)
        self.assertIn("if (Time.time >= nextDamageTick)", update)
        self.assertIn("if (boundary != null && Time.time >= nextRingDraw)", update)


if __name__ == "__main__":
    unittest.main()
