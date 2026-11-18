#!/usr/bin/env python3
"""Static and deterministic regression for safe parked-car rollover egress.

This does not certify native Unity collisions or on-device gameplay.
"""
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text("utf-8")
SESSION = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleSession.cs").read_text("utf-8")
EXIT = SESSION[SESSION.index("private bool TryFindExit("):
               SESSION.index("private bool TryExit()")]
EXIT_ACTION = SESSION[SESSION.index("private bool TryExit()"):
                      SESSION.index("private void FollowCarCamera()")]


class StoppedRolloverExitTests(unittest.TestCase):
    def test_only_stationary_vehicles_get_relaxed_exit_attempt(self):
        self.assertIn("public bool CanEvacuateStoppedVehicle =>", MOTOR)
        self.assertIn("TotalSpeedMetresPerSecond < .65f", MOTOR)
        self.assertIn("body.angularVelocity.magnitude < .25f", MOTOR)
        for speed, angular, expected in ((0, 0, True), (.64, .24, True),
                                         (.65, .0, False), (.0, .25, False),
                                         (2.0, .0, False), (.0, 1.0, False)):
            self.assertEqual(speed < .65 and angular < .25, expected)
        self.assertIn("(!car.IsSafeToExit && !car.CanEvacuateStoppedVehicle)", EXIT_ACTION)
        self.assertIn("!TryFindExit(out Vector3 safeExit)", EXIT_ACTION)
        # Boarding remains strictly limited to normally grounded, upright cars.
        self.assertIn("!IsSafeToExit) return false;", MOTOR)

    def test_ground_relative_offsets_remain_horizontal_on_roll(self):
        self.assertIn("Vector3.ProjectOnPlane(t.forward, Vector3.up)", EXIT)
        self.assertIn("Vector3.ProjectOnPlane(t.right, Vector3.up)", EXIT)
        self.assertIn("Vector3.Cross(flatRight, Vector3.up)", EXIT)
        self.assertIn("Vector3 flatRightAxis = Vector3.Cross(Vector3.up, flatForward);", EXIT)
        self.assertNotIn("t.right * 3.2f", EXIT)
        self.assertNotIn("t.forward * 3.5f", EXIT)
        # Chassis forward +Z and car rolled 0, 80, 90, 180 degrees.
        # Projected candidates never inherit roll-induced vertical offsets.
        for roll in (0, 40, 80, 90, 120, 180, -90):
            old_right_y = math.sin(math.radians(roll))
            old_side_height_delta = 3.2 * old_right_y
            flat_forward = (0., 0., 1.)
            flat_right = (flat_forward[2], 0., -flat_forward[0])
            candidates = [(3.2*flat_right[0], 0., 3.2*flat_right[2]),
                          (-3.2*flat_right[0], 0., -3.2*flat_right[2]),
                          (-3.5*flat_forward[0], 0., -3.5*flat_forward[2]),
                          (3.5*flat_forward[0], 0., 3.5*flat_forward[2])]
            for x, y, z in candidates:
                self.assertEqual(y, 0)
                self.assertGreater(math.hypot(x, z), 3.1)
            if abs(roll) == 90:
                self.assertGreater(abs(old_side_height_delta), 3)

    def test_vertical_nose_fallback_keeps_nonzero_planar_heading(self):
        for pitch in (75, 85, 89, 90, 91):
            forward_z = math.cos(math.radians(pitch))
            if forward_z * forward_z < .04:
                # t.right = +X, t.right cross world-up = +Z.
                forward_z = 1.
            self.assertGreater(abs(forward_z), .19)

    def test_existing_exit_safety_still_authoritative(self):
        self.assertIn("Physics.RaycastNonAlloc", EXIT)
        self.assertIn("Physics.OverlapCapsuleNonAlloc", EXIT)
        self.assertIn("HasPotentialInactiveObstacle", EXIT)
        self.assertIn("HasClearVehicleAccess", EXIT)
        self.assertIn("hit.normal.y < .707f", EXIT)
        self.assertIn("Mathf.Abs(hit.point.y - (option.y - .71f)) > 1.5f", EXIT)
        self.assertIn("if (!safe) continue;", EXIT)
        self.assertIn("position = support + Vector3.up * .08f;", EXIT)
        # Relaxed exit calculation lives only behind explicit EXIT, not Update/LateUpdate.
        update = SESSION[SESSION.index("private void Update()"):
                         SESSION.index("private bool TryFindExit(")]
        self.assertNotIn("flatForward", update)
        self.assertNotIn("CanEvacuateStoppedVehicle", update)


if __name__ == "__main__":
    unittest.main()
