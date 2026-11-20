#!/usr/bin/env python3
"""Pure-policy tests for Android roadside bike model prewarming.

These do not simulate Unity, verify textures or measure a physical phone's FPS.
"""
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisRoadsideBikeScenery.cs").read_text(
    encoding="utf-8"
)


def predict(previous, current, elapsed):
    """Horizontal 1D projection matching the Android-only bounded policy."""
    if not 0.2 <= elapsed <= 1.5:
        return current
    delta = current - previous
    if abs(delta) > 45.0 * elapsed:
        return current
    velocity = delta / elapsed
    if velocity * velocity <= 1.0:
        return current
    return current + max(-28.0, min(28.0, velocity)) * 1.25


def should_prewarm(player, projected, bay):
    """One existing model resource; no predicted bike spawning."""
    return abs(bay - player) <= 110.0 or abs(bay - projected) <= 110.0


class BikePredictivePrewarm(unittest.TestCase):
    def test_fast_approach_imports_before_visibility_without_new_spawns(self):
        future = predict(-9.0, 0.0, 0.3)
        self.assertEqual(future, 35.0)
        self.assertTrue(should_prewarm(0.0, future, 135.0))
        self.assertFalse(should_prewarm(0.0, 0.0, 135.0))
        self.assertGreater(135.0, 45.0)

    def test_paused_teleport_idle_vertical_and_speed_clamp(self):
        self.assertEqual(predict(0.0, 500.0, 0.4), 500.0)
        self.assertEqual(predict(0.0, 30.0, 4.0), 30.0)
        self.assertEqual(predict(0.0, 0.0, 0.4), 0.0)
        self.assertEqual(predict(-0.1, 0.0, 0.4), 0.0)
        self.assertEqual(predict(-20.0, 0.0, 0.5), 35.0)
        self.assertEqual(predict(20.0, 0.0, 0.5), -35.0)
        self.assertFalse(should_prewarm(0, 35, 160))

    def test_only_optional_android_import_uses_prediction(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR\n        // Predict only", SOURCE)
        self.assertIn("BikePrewarmLeadSeconds = 1.25f;", SOURCE)
        self.assertIn("BikePrewarmMaxSpeedMetresPerSecond = 28f;", SOURCE)
        self.assertIn("BikePrewarmTeleportSpeedMetresPerSecond = 45f;", SOURCE)
        self.assertIn("movement.y = 0f;", SOURCE)
        self.assertIn("elapsed >= 0.2f && elapsed <= 1.5f", SOURCE)
        self.assertIn("movement.sqrMagnitude <= teleportLimit * teleportLimit", SOURCE)
        self.assertIn("Vector3.ClampMagnitude(", SOURCE)
        segment = SOURCE[
            SOURCE.index("Vector3 predictedPrewarmPosition = position;"):
            SOURCE.index("if (bikeSource == null)", SOURCE.index("Vector3 predictedPrewarmPosition = position;"))
        ]
        for forbidden in ("Physics.", "Instantiate(", "Resources.Load", "new GameObject", "Destroy("):
            self.assertNotIn(forbidden, segment)
        self.assertLess(SOURCE.index("bool nearbyNow ="), SOURCE.index("bool approaching ="))
        self.assertIn("if (!nearbyNow && !approaching) continue;", SOURCE)
        self.assertIn("AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(ModelResource)", SOURCE)

    def test_preserve_original_vision_and_lifecycle_boundaries(self):
        for invariant in (
            "private const int MaximumVisibleBikes = 3;",
            "private const float LoadRadius = 45f;",
            "private const float PrewarmRadius = 110f;",
            "private const float UnloadRadius = 66f;",
            "parkedBikePool.Push(retired);",
            "ReuseBike(parkedBikePool.Pop(), nearest.point, spot)",
            "if (plan.visible != null || now < plan.nextProbe) continue;",
            "TryParkingSpot(nearest.point, out Vector3 spot)",
        ):
            self.assertIn(invariant, SOURCE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
