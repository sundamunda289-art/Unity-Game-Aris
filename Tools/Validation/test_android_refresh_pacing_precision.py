#!/usr/bin/env python3
"""Deterministic Android display pacing contract; not a Unity/FPS benchmark.

An exact multiple of the requested rate can arrive with tiny refresh-clock
rounding noise. The GPU quality governor and the shared optional import gate
must agree on the same effective presentation budget.
"""
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT_DIR = ROOT / "Assets" / "AarisWorld" / "Scripts"
RUNTIME = (SCRIPT_DIR / "AarisMobileRuntime.cs").read_text(encoding="utf-8")
GATE = (SCRIPT_DIR / "AarisMobileStreamingFrameGate.cs").read_text(encoding="utf-8")


def effective_fps(refresh_hz, requested_fps):
    """Reference policy mirrored by both Android C# implementations."""
    requested_fps = requested_fps if requested_fps > 0 else 30
    if not math.isfinite(refresh_hz) or refresh_hz < 1:
        return requested_fps
    divisor = max(1, math.ceil(refresh_hz / requested_fps))
    nearest = math.floor(refresh_hz / requested_fps + 0.5)
    if nearest >= 1 and abs(refresh_hz - nearest * requested_fps) <= 0.05:
        divisor = nearest
    return refresh_hz / divisor


class AndroidRefreshPrecision(unittest.TestCase):
    def test_two_controllers_agree_on_precision_guard(self):
        self.assertIn("float.IsNaN(refreshHz) || float.IsInfinity(refreshHz)", RUNTIME)
        self.assertIn("refreshHz < 1f) return requestedFps;", RUNTIME)
        self.assertIn("Mathf.CeilToInt(refreshHz / requestedFps)", RUNTIME)
        self.assertIn("Mathf.RoundToInt(refreshHz / requestedFps)", RUNTIME)
        self.assertIn("Mathf.Abs(refreshHz - nearestDivisor * requestedFps) <= 0.05f", RUNTIME)
        self.assertIn("!double.IsNaN(refreshHz) && !double.IsInfinity(refreshHz)", GATE)
        self.assertIn("System.Math.Ceiling(refreshHz / requestedFps)", GATE)
        self.assertIn("System.Math.Round(refreshHz / requestedFps)", GATE)
        self.assertIn("System.Math.Abs(refreshHz - nearestDivisor * requestedFps) <= 0.05d", GATE)
        self.assertIn("return CurrentFrameBudgetSeconds(Time.frameCount);", GATE)

    def test_clock_noise_does_not_force_extra_display_refresh(self):
        self.assertAlmostEqual(effective_fps(90.0001, 30), 30.00003333333, places=5)
        self.assertAlmostEqual(effective_fps(120.0001, 60), 60.00005, places=5)
        self.assertAlmostEqual(effective_fps(60.0001, 30), 30.00005, places=5)
        self.assertAlmostEqual(effective_fps(89.9999, 30), 29.99996666667, places=5)

    def test_actual_nondivisor_modes_stay_at_real_pacing(self):
        self.assertAlmostEqual(effective_fps(90, 60), 45)
        self.assertAlmostEqual(effective_fps(59.94, 30), 29.97)
        self.assertAlmostEqual(effective_fps(120, 90), 60)
        self.assertAlmostEqual(effective_fps(144, 60), 48)

    def test_invalid_refresh_does_not_poison_quality_governor(self):
        for invalid in (float("nan"), float("inf"), -float("inf"), -1, 0, 0.5):
            self.assertEqual(effective_fps(invalid, 30), 30)
            self.assertEqual(effective_fps(invalid, 60), 60)
        self.assertEqual(effective_fps(90, -1), 30)

    def test_android_only_and_no_fidelity_assets_changed(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", GATE)
        self.assertIn("private static float EffectiveAndroidTargetFps(", RUNTIME)
        runtime_policy = RUNTIME.split(
            "private static float EffectiveAndroidTargetFps(", 1
        )[1].split("private void TuneAndroidResolution(", 1)[0]
        self.assertNotIn("renderScale =", runtime_policy)
        self.assertNotIn("QualitySettings.", runtime_policy)
        self.assertNotIn("Instantiate(", runtime_policy)
        self.assertNotIn("Resources.Load(", runtime_policy)


if __name__ == "__main__":
    unittest.main(verbosity=2)
