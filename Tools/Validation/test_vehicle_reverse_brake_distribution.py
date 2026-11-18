#!/usr/bin/env python3
"""Signed longitudinal brake distribution regression for offline vehicles.

Source/geometry contracts only; actual Unity WheelCollider and Android gameplay
remain subject to a native Play Mode test.
"""
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()


def direction_of_braking(driver, brake, longitudinal_speed):
    """Independent numerical model of the source's existing torque gates."""
    if not driver or not 500 <= brake < 3600:
        return 0
    if longitudinal_speed > 2.0:
        return 1
    if longitudinal_speed < -2.0:
        return -1
    return 0


def brake_biases(direction):
    return [
        1.0 if direction == 0 else
        (1.12 if (i < 2) == (direction > 0) else .88)
        for i in range(4)
    ]


class DirectionalBrakeLoadTransfer(unittest.TestCase):
    def test_forward_reverse_and_sideways_stops(self):
        for speed, expected in (
            (20.0, 1), (2.001, 1), (2.0, 0),
            (0.0, 0), (-2.0, 0), (-2.001, -1), (-7.0, -1),
        ):
            with self.subTest(speed=speed):
                self.assertEqual(direction_of_braking(True, 3200, speed), expected)
        self.assertEqual(brake_biases(1), [1.12, 1.12, .88, .88])
        self.assertEqual(brake_biases(-1), [.88, .88, 1.12, 1.12])
        self.assertEqual(brake_biases(0), [1.0] * 4)

        # High lateral speed must not masquerade as forward/reverse travel.
        # Local velocity is (lateral, vertical, longitudinal) in metres/s.
        for v in ((25, 0, 0), (-25, 0, .5), (0, 10, -1)):
            self.assertEqual(direction_of_braking(True, 3200, v[2]), 0)

    def test_parking_and_idle_coasting_stay_balanced(self):
        for driver in (False, True):
            for brake in (0, 155, 499, 3600, 5000):
                for longitudinal in (-25, -3, 3, 25):
                    self.assertEqual(
                        direction_of_braking(driver, brake, longitudinal), 0
                    )
        for brake in (500, 1100, 3200):
            for speed in (-20, 20):
                self.assertEqual(direction_of_braking(False, brake, speed), 0)

    def test_total_brake_torque_conserved_each_direction(self):
        for brake in (500, 1100, 3200, 3599):
            for direction in (-1, 0, 1):
                actual = [brake * bias for bias in brake_biases(direction)]
                self.assertAlmostEqual(sum(actual), 4 * brake)
                self.assertTrue(all(math.isfinite(v) for v in actual))
                self.assertTrue(all(v >= 0 for v in actual))
        self.assertNotEqual(brake_biases(-1), brake_biases(1))

    def test_source_cache_refreshes_while_brake_held_on_direction_flip(self):
        s = MOTOR.split("private void ApplyWheelForces(", 1)[1].split(
            "private static float ContinuousDriveTorque(", 1)[0]
        for line in (
            "int brakeDirection = 0;",
            "float longitudinalSpeed = SpeedMetresPerSecond;",
            "if (longitudinalSpeed > 2f) brakeDirection = 1;",
            "else if (longitudinalSpeed < -2f) brakeDirection = -1;",
            "brakeDirection != appliedServiceBrakeDirection;",
            "appliedServiceBrakeDirection = brakeDirection;",
            "wheels[i].brakeTorque = brake * bias;",
        ):
            self.assertIn(line, s)
        self.assertNotIn("frontBiased", s)
        self.assertNotIn("body.linearVelocity.sqrMagnitude > 4f", s)
        self.assertLess(s.index("brakeDirection != appliedServiceBrakeDirection;"),
                        s.index("if (!driveChanged && !brakeChanged) return;"))
        self.assertLess(s.index("wheels[i].brakeTorque = brake * bias;"),
                        s.index("appliedServiceBrakeDirection = brakeDirection;"))

    def test_no_extra_physics_query_or_runtime_allocation(self):
        s = MOTOR.split("private void ApplyWheelForces(", 1)[1].split(
            "private static float ContinuousDriveTorque(", 1)[0]
        self.assertIn("private int appliedServiceBrakeDirection;", MOTOR)
        self.assertIn("appliedServiceBrakeDirection = 0;", MOTOR)
        for forbidden in (
            "Physics.Raycast", "Physics.Overlap", "GetGroundHit(",
            "new GameObject(", "new Material(", "new float[", "new List<",
            "renderer.bounds", "Rigidbody.AddForce(",
        ):
            self.assertNotIn(forbidden, s)
        self.assertIn("SpeedMetresPerSecond => body != null", MOTOR)


if __name__ == "__main__":
    unittest.main()
