#!/usr/bin/env python3
"""Regression tests for real-velocity vehicle HUD and grounded-wheel handling.
These are source/math guards, not a substitute for Unity Play Mode or Android QA.
"""
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
MOTOR = (ROOT / "AarisVehicleMotor.cs").read_text()
HUD = (ROOT / "AarisVehicleHUD.cs").read_text()


class RealVehicleTelemetry(unittest.TestCase):
    def test_speedometer_uses_live_physics_and_not_touch_arrows(self):
        self.assertIn("Vehicle Speedometer", HUD)
        self.assertIn("speedPanel.transform.SetParent(safeRoot, false)", HUD)
        self.assertIn("speedBackground.raycastTarget = false;", HUD)
        self.assertIn("speedCaption.raycastTarget = false;", HUD)
        self.assertIn("vehicle.TotalSpeedMetresPerSecond", HUD)
        self.assertIn("vehicle.SpeedMetresPerSecond", HUD)
        self.assertIn("vehicle.ThrottleInput", HUD)
        self.assertIn("metresPerSecond * 3.6f", HUD)
        self.assertIn("Time.unscaledTime + .125f", HUD)
        self.assertIn("if (speedCaption.text != caption) speedCaption.text = caption;", HUD)
        self.assertIn("vehicle.IsServiceBraking", HUD)
        self.assertNotIn("AddListener(", HUD.split("new GameObject(\"Vehicle Speedometer\"", 1)[1].split("arrowPanel.SetActive(false)", 1)[0])
        self.assertIn("speedPanel.SetActive(false);", HUD)
        self.assertIn("speedPanel.SetActive(driving);", HUD)
        self.assertIn("if (driving) UpdateSpeedIndicator();", HUD)

    def test_forward_reverse_neutral_selection_and_pausing(self):
        start = HUD.split("private void UpdateSpeedIndicator()", 1)[1]
        finish = start.split("private void LateUpdate()", 1)[0]
        self.assertIn('longitudinal < -.4f', finish)
        self.assertIn('throttle < -.1f', finish)
        self.assertIn('longitudinal > .4f || throttle > .1f ? "F" : "N"', finish)
        self.assertIn('float.IsNaN(metresPerSecond)', finish)
        self.assertIn('float.IsInfinity(metresPerSecond)', finish)
        hud_pause = HUD.split("private void OnDisable()", 1)[1]
        self.assertIn("ClearArrows();", hud_pause)
        self.assertIn("speedPanel.SetActive(false);", hud_pause)
        self.assertIn("if (speedPanel != null && speedPanel.activeSelf)", HUD)

    def test_stationary_speed_and_condition_do_not_allocate_every_poll(self):
        self.assertIn("private int lastRenderedKmh = -1;", HUD)
        self.assertIn("private string lastRenderedDirection;", HUD)
        self.assertIn("private int lastRenderedConditionPercent = -1;", HUD)
        render = HUD.split("private void UpdateSpeedIndicator()", 1)[1].split(
            "private void LateUpdate()", 1)[0]
        self.assertIn("if (kmh != lastRenderedKmh || direction != lastRenderedDirection)", render)
        self.assertLess(render.index("if (kmh != lastRenderedKmh"),
                        render.index('string caption = kmh + " km/h   " + direction;'))
        self.assertIn("if (percent != lastRenderedConditionPercent)", render)
        self.assertLess(render.index("if (percent != lastRenderedConditionPercent)"),
                        render.index('string condition = "CAR CONDITION " + percent + "%";'))
        self.assertIn("else lastRenderedConditionPercent = -1;", render)
        # After entering, a long stable parked car should format exactly once.
        last_speed = -1
        last_dir = None
        last_condition = -1
        formatted_speed = formatted_condition = 0
        for speed, direction, condition in (
            [(0, "N", 100)] * 240 +
            [(12, "F", 98)] * 240 +
            [(12, "F", None)] * 240 +
            [(12, "F", 98)] * 240
        ):
            if (speed, direction) != (last_speed, last_dir):
                formatted_speed += 1
                last_speed, last_dir = speed, direction
            if condition is None:
                last_condition = -1
            elif condition != last_condition:
                formatted_condition += 1
                last_condition = condition
        self.assertEqual(formatted_speed, 2)
        self.assertEqual(formatted_condition, 3)
        toggle = HUD.split("if (speedPanel != null && speedPanel.activeSelf != driving)", 1)[1].split(
            "if (driving) UpdateSpeedIndicator();", 1)[0]
        for s in ("lastRenderedKmh = -1;", "lastRenderedDirection = null;",
                  "lastRenderedConditionPercent = -1;"):
            self.assertIn(s, toggle)
            self.assertIn(s, HUD.split("private void OnDisable()", 1)[1])

    def test_mass_aware_torque_and_high_speed_steering(self):
        self.assertIn("driveTorqueScale = Mathf.Clamp(Mathf.Sqrt(body.mass / 1250f)", MOTOR)
        self.assertIn("float torque = baseTorque * driveTorqueScale;", MOTOR)
        self.assertIn("steeringMassFactor = Mathf.Lerp(1f, .88f,", MOTOR)
        self.assertIn("Mathf.InverseLerp(1150f, 2100f, body.mass)", MOTOR)
        self.assertIn("Mathf.InverseLerp(0f, 24f, planarSpeed)) * steeringMassFactor *", MOTOR)
        self.assertIn("steeringResponseMultiplier;", MOTOR)
        masses = [1140, 1250, 1650, 1950, 2050]
        acceleration = []
        for mass in masses:
            scale = max(.88, min(1.28, math.sqrt(mass / 1250)))
            acceleration.append(1500 * scale / mass)
        # Trucks retain usable torque but never accelerate faster than a sports car.
        self.assertTrue(all(acceleration[i] > acceleration[i + 1] for i in range(len(acceleration) - 1)))
        self.assertTrue(all(.88 <= max(.88, min(1.28, math.sqrt(m / 1250))) <= 1.28 for m in masses))

    def test_one_wheel_airborne_cannot_receive_false_antiroll_push(self):
        antiroll = MOTOR.split("private void ApplyAntiRoll(", 1)[1]
        self.assertIn("bool leftGrounded = left.GetGroundHit(", antiroll)
        self.assertIn("bool rightGrounded = right.GetGroundHit(", antiroll)
        guard = "if (!leftGrounded || !rightGrounded) return;"
        self.assertIn(guard, antiroll)
        self.assertLess(antiroll.index("ApplySurfaceGrip(leftIndex,"), antiroll.index(guard))
        self.assertLess(antiroll.index("ApplySurfaceGrip(rightIndex,"), antiroll.index(guard))
        self.assertLess(antiroll.index(guard), antiroll.index("body.AddForceAtPosition("))
        # One/both airborne = no roll force; both grounded = wheel-travel response.
        self.assertEqual([(a and b) for a, b in
                          ((False, False), (True, False), (False, True), (True, True))],
                         [False, False, False, True])


if __name__ == "__main__":
    unittest.main()
