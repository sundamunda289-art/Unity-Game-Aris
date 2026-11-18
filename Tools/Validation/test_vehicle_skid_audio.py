#!/usr/bin/env python3
"""Regression checks for believable tyre audio during true lateral skids.
Static contract only; native Unity audio and Android measurements still required.
"""
from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[2]
AUDIO = (ROOT / "Assets/AarisWorld/Scripts/AarisAudioDirector.cs").read_text()
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()

class VehicleAudioSkid(unittest.TestCase):
    def test_road_speed_controls_skid_cue_not_signed_longitudinal_speed(self):
        segment = AUDIO.split("private void TickVehicle(bool driving, float dt)", 1)[1].split(
            "private float FootfallSpacing(", 1)[0]
        self.assertIn("float roadSpeed = car.TotalSpeedMetresPerSecond;", segment)
        self.assertIn("bool tyreSkidding = roadSpeed > 4f && tyreSlip > 0.24f;", segment)
        self.assertNotIn("bool tyreSkidding = speed > 4f", segment)
        self.assertIn("float tyreSlip = car.TyreSlip;", segment)
        self.assertIn("float gearPhase = signedSpeed < -0.2f", segment)
        self.assertIn("float speed = Mathf.Abs(car.SpeedMetresPerSecond);", segment)
        self.assertIn('OneShot("tire_skid", car.transform.position,', segment)

    def test_road_speed_and_slip_remain_physics_authoritative(self):
        self.assertIn("public float TotalSpeedMetresPerSecond => body != null", MOTOR)
        self.assertIn("? body.linearVelocity.magnitude : 0f;", MOTOR)
        self.assertIn("public float TyreSlip => tyreSlip;", MOTOR)
        fixed = MOTOR.split("private void FixedUpdate()", 1)[1].split(
            "private void ApplyAckermannSteering(", 1)[0]
        self.assertIn("tyreSlip = 0f;", fixed)
        self.assertIn("ApplyAntiRoll(0, 1);", fixed)
        self.assertIn("ApplyAntiRoll(2, 3);", fixed)

if __name__ == "__main__":
    unittest.main()
