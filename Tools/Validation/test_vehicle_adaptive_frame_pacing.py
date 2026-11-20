#!/usr/bin/env python3
"""Source and deterministic vehicle-stream budget checks, not Android FPS proof."""
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()
GATE = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileStreamingFrameGate.cs").read_text()


def simulate(frame_delta, outcomes, display_hz=60, requested_fps=60):
    """Mirror Android display-divisor pacing plus the 8% jitter allowance."""
    assert display_hz > 0 and requested_fps > 0
    paced_fps = display_hz / math.ceil(display_hz / requested_fps)
    constrained = frame_delta > 1.08 / paced_fps
    built = probes = 0
    for success in outcomes:
        if not (built < 2 and probes < 8 and
                (not constrained or (built == 0 and probes < 3))):
            break
        probes += 1
        built += int(success)
    return built, probes


class AndroidVehiclePacing(unittest.TestCase):
    def test_guard_source(self):
        block = WORLD.split("private void Update()", 1)[1].split(
            "private AarisVehicleRecord FarthestReplaceableParkedCar(", 1)[0]
        for required in (
            "#if UNITY_ANDROID && !UNITY_EDITOR",
            "AarisMobileStreamingFrameGate.CurrentPacedFrameBudgetSeconds() * 1.08f",
            "const bool constrainedFrame = false;",
            "(!constrainedFrame || (built == 0 && spawnProbes < 3))",
        ):
            self.assertIn(required, block)
        self.assertNotIn("Time.smoothDeltaTime > (1f / 45f)", block)
        self.assertIn("internal static float CurrentPacedFrameBudgetSeconds()", GATE)
        self.assertIn("return CurrentFrameBudgetSeconds(Time.frameCount);", GATE)
        self.assertIn("Screen.currentResolution.refreshRateRatio.value", GATE)
        self.assertIn("System.Math.Ceiling(refreshHz / requestedFps)", GATE)
        self.assertLess(
            block.index("nearestDistanceSq = distanceSq;"),
            block.index("if (!TryResolvePlanned(record, out Vector3 pos))"))
        self.assertLess(
            block.index("if (!Build(record, pos))"),
            block.index("Destroy(retired.gameObject);"))
        self.assertNotIn("renderScale", block)

    def test_budgets_at_60hz(self):
        self.assertEqual(simulate(1 / 60, [True] * 9), (2, 2))
        self.assertEqual(simulate(1 / 30, [True] * 9), (1, 1))
        self.assertEqual(simulate(1 / 30, [False] * 9), (0, 3))
        self.assertEqual(simulate(1 / 15, [False, True, True]), (1, 2))

    def test_true_30fps_is_not_always_slow(self):
        self.assertEqual(simulate(1 / 30, [True] * 9, requested_fps=30), (2, 2))
        self.assertEqual(simulate(1 / 30, [False] * 9, requested_fps=30), (0, 8))
        self.assertEqual(simulate(1 / 25, [False] * 9, requested_fps=30), (0, 3))

    def test_90hz_request_60_uses_actual_45fps(self):
        self.assertEqual(simulate(1 / 45, [True] * 9, display_hz=90), (2, 2))
        self.assertEqual(simulate(1.06 / 45, [False] * 9, display_hz=90), (0, 8))
        self.assertEqual(simulate(1.2 / 45, [False] * 9, display_hz=90), (0, 3))

    def test_120hz_request_60_uses_true_60fps(self):
        self.assertEqual(simulate(1 / 60, [True] * 9, display_hz=120), (2, 2))
        self.assertEqual(simulate(1 / 45, [True] * 9, display_hz=120), (1, 1))


if __name__ == "__main__":
    unittest.main()
