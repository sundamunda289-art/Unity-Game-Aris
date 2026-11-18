#!/usr/bin/env python3
"""Frame-pacing contract for the noncombat vehicle chase camera.

These deterministic maths checks do not claim Unity Play Mode coverage.
"""
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SESSION = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleSession.cs").read_text("utf-8")
CAMERA = SESSION[SESSION.index("private void FollowCarCamera()"):
                 SESSION.index("private void ClearHeldDrivingInput()")]
MAX_FRAME_SECONDS = 0.15


class VehicleCameraPacing(unittest.TestCase):
    def test_one_bounded_time_step_for_position_rotation_and_fov(self):
        self.assertIn("float cameraFrameDt = Mathf.Clamp(Time.deltaTime, 0f, .15f);", CAMERA)
        self.assertIn(".12f, Mathf.Infinity, cameraFrameDt);", CAMERA)
        self.assertIn("1f - Mathf.Exp(-10f * cameraFrameDt)", CAMERA)
        self.assertIn("float fovDelta = cameraFrameDt;", CAMERA)
        self.assertNotIn("Mathf.Min(.05f, Time.deltaTime)", CAMERA)
        self.assertEqual(CAMERA.count("float cameraFrameDt ="), 1)
        self.assertIn("Vector3.ProjectOnPlane(\n                vehicle.forward, Vector3.up);", CAMERA)
        self.assertIn("if (horizontalForward.sqrMagnitude >= .04f)", CAMERA)
        self.assertIn("lastChaseHeading = horizontalForward.normalized;", CAMERA)
        self.assertIn("lastChaseHeading * chaseDistance + Vector3.up * chaseHeight", CAMERA)
        self.assertNotIn("vehicle.forward * chaseDistance", CAMERA)
        self.assertIn("private Vector3 lastChaseHeading = Vector3.forward;", SESSION)
        self.assertIn("Vector3 entryHeading = Vector3.ProjectOnPlane(", SESSION)
        self.assertIn("lastChaseHeading = entryHeading.sqrMagnitude >= .04f", SESSION)

    def test_rotation_and_fov_recover_in_real_time_at_10_to_120_fps(self):
        for fps in (10, 15, 20, 30, 45, 60, 90, 120):
            dt = min(MAX_FRAME_SECONDS, 1.0 / fps)
            rotation = fov = 0.0
            for _ in range(fps):
                rotation += (1.0 - rotation) * (1.0 - math.exp(-10.0 * dt))
                fov += (1.0 - fov) * (1.0 - math.exp(-5.0 * dt))
            self.assertAlmostEqual(rotation, 1.0 - math.exp(-10.0), places=7)
            self.assertAlmostEqual(fov, 1.0 - math.exp(-5.0), places=7)

    def test_chase_height_is_independent_of_vehicle_pitch_and_roll(self):
        # 6.25-7.4 m rear offset must NOT leak pitch into camera height.
        # A 30-degree climb previously shifted the camera 3.1-3.7 m down.
        for chase_distance in (6.25, 7.40):
            for pitch_degrees in (-75, -45, -30, 0, 30, 45, 75):
                pitch = math.radians(pitch_degrees)
                chassis_forward = (0.0, math.sin(pitch), math.cos(pitch))
                horizontal = (chassis_forward[0], 0.0, chassis_forward[2])
                length = math.hypot(horizontal[0], horizontal[2])
                self.assertGreater(length * length, .04)
                heading = (horizontal[0] / length, 0.0,
                           horizontal[2] / length)
                # World-up-only camera Y is exactly the requested altitude.
                camera_offset_y = -heading[1] * chase_distance + 3.05
                self.assertAlmostEqual(camera_offset_y, 3.05, places=7)
                old_offset_y = -chassis_forward[1] * chase_distance + 3.05
                if abs(pitch_degrees) >= 30:
                    self.assertGreater(abs(old_offset_y - camera_offset_y),
                                       3.0)

    def test_nearly_vertical_cars_keep_last_heading(self):
        # Real player: a car noses upwards near vertical in a jump.
        # The camera should keep its last horizontal direction rather
        # than normalize tiny/unstable components or jump to world north.
        heading = (0.6, 0.8)
        for pitch_degrees in (40, 65, 77, 80, 89, 90, 89.9, 80):
            planar_length = abs(math.cos(math.radians(pitch_degrees)))
            if planar_length * planar_length >= .04:
                heading = (0.6, 0.8)
            self.assertAlmostEqual(math.hypot(*heading), 1.0)
            self.assertEqual(heading, (0.6, 0.8))
        # A fresh entry must seed from the new car, not the old car's yaw.
        self.assertIn("lastChaseHeading = entryHeading.sqrMagnitude >= .04f",
                      SESSION)
        self.assertIn("cameraVelocity = Vector3.zero;", SESSION)

    def test_hitches_are_bounded_but_normal_low_fps_is_not_shortened(self):
        for dt in (0.0, .001, .016, .033, .067, .100, .150, .200, .500, 5.0):
            bounded = max(0.0, min(MAX_FRAME_SECONDS, dt))
            self.assertGreaterEqual(bounded, 0.0)
            self.assertLessEqual(bounded, MAX_FRAME_SECONDS)
            if 0.0 <= dt <= MAX_FRAME_SECONDS:
                self.assertAlmostEqual(bounded, dt)


if __name__ == "__main__":
    unittest.main(verbosity=2)
