#!/usr/bin/env python3
"""Read-only source and deterministic policy checks for vehicle-only prefetch.

This is not a Unity Editor compilation or on-device FPS benchmark.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text(
    encoding="utf-8"
)


def predict(previous, current, elapsed):
    """Mirror the Android-only bounded lookahead policy on one horizontal axis."""
    if not (0.15 <= elapsed <= 1.5):
        return current
    movement = current - previous
    if abs(movement) > 45.0 * elapsed:
        return current
    velocity = movement / elapsed
    if velocity * velocity <= 1.0:
        return current
    return current + max(-32.0, min(32.0, velocity))


def choose_nearby_before_prediction(positions, loaded, current, predicted):
    """Model the one optional asset request using real position as priority."""
    for origin in (current, predicted):
        candidates = [
            (abs(x - origin), i)
            for i, x in enumerate(positions)
            if not loaded[i] and abs(x - origin) <= 150.0
        ]
        if candidates:
            return min(candidates)[1]
    return None


class VehiclePredictivePrefetchTests(unittest.TestCase):
    def test_actual_nearby_cars_have_priority(self):
        # Actual car at +149m is admitted before a predicted car at +172m.
        self.assertEqual(
            choose_nearby_before_prediction([149, 172], [False, False], 0, 25), 0
        )
        self.assertEqual(
            choose_nearby_before_prediction([149, 172], [True, False], 0, 25), 1
        )

    def test_approach_can_preload_while_invisible(self):
        projected = predict(-9, 0, 0.3)  # 30 m/s driving
        self.assertEqual(projected, 30)
        self.assertEqual(
            choose_nearby_before_prediction([175], [False], 0, projected), 0
        )
        self.assertIsNone(
            choose_nearby_before_prediction([175], [False], 0, 0)
        )

    def test_teleport_pause_stationary_and_bounded_speed(self):
        self.assertEqual(predict(0, 500, 0.3), 500)
        self.assertEqual(predict(0, 30, 4.0), 30)
        self.assertEqual(predict(0, 0, 0.3), 0)
        self.assertEqual(predict(-12, 0, 0.3), 32)
        self.assertEqual(predict(12, 0, 0.3), -32)
        self.assertEqual(predict(-0.3, 0, 0.3), 0)  # <= 1 m/s

    def test_is_android_only_and_keeps_real_position_for_gameplay(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR\n        // Prediction", SOURCE)
        self.assertIn("VehiclePrewarmLeadSeconds = 1f;", SOURCE)
        self.assertIn("VehiclePrewarmMaxSpeedMetresPerSecond = 32f;", SOURCE)
        self.assertIn("VehiclePrewarmTeleportSpeedMetresPerSecond = 45f;", SOURCE)
        self.assertIn("elapsed >= 0.15f && elapsed <= 1.5f", SOURCE)
        self.assertIn("movement.sqrMagnitude <= teleportLimit * teleportLimit", SOURCE)
        self.assertIn("Vector3.ClampMagnitude(", SOURCE)
        self.assertIn("if (closestType < 0 && predictedPrewarmPosition != playerPosition)", SOURCE)
        self.assertLess(
            SOURCE.index("if (closestType < 0 && predictedPrewarmPosition != playerPosition)"),
            SOURCE.index("if (closestType >= 0) StartAuthoredBodyPrewarm(closestType);")
        )
        future_scan = SOURCE[
            SOURCE.index("if (closestType < 0 && predictedPrewarmPosition != playerPosition)"):
            SOURCE.index("if (closestType >= 0) StartAuthoredBodyPrewarm(closestType);")
        ]
        self.assertNotIn("Instantiate(", future_scan)
        self.assertNotIn("Resources.Load", future_scan)
        self.assertNotIn("Physics.", future_scan)
        self.assertNotIn("new GameObject", future_scan)
        self.assertIn("private const float LoadRadius = 105f;", SOURCE)
        self.assertIn("private const float AuthoredPrewarmRadius = 150f;", SOURCE)
        self.assertIn("private const float UnloadRadius = 160f;", SOURCE)
        self.assertIn("private const int MaximumNearbyVehicles = 10;", SOURCE)
        self.assertIn(
            "(record.visible.transform.position - playerPosition).sqrMagnitude",
            SOURCE
        )
        self.assertIn("(pending.position - playerPosition).sqrMagnitude", SOURCE)
        self.assertIn("AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(", SOURCE)


if __name__ == "__main__":
    unittest.main()
