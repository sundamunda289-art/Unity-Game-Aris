#!/usr/bin/env python3
"""Fitted-car chase camera framing, not a fixed sedan-height camera.

Mathematical/source safeguards; native Play Mode/Android capture is separate.
"""
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SESSION = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleSession.cs").read_text()
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()

def frame(length, center, height, speed):
    # Keep reference maths identical to the bounded Unity camera policy.
    length = max(3., min(5.3, length))
    roof = center + height * .5
    aim = max(.68, min(1.65, max(.68, min(max(.78, roof+.12),
                                center+height*.62))))
    distance_delta = max(-.24, min(.36, (length-4.20)*.26))
    height_delta = max(-.15, min(.10, (aim-1.30)*.25))
    distance = 6.25+(7.40-6.25)*speed+distance_delta
    chase_height = 2.75+(3.05-2.75)*speed+height_delta
    aim_at_speed = aim+.12*speed
    return aim_at_speed, chase_height, distance

class CarCameraMetricFraming(unittest.TestCase):
    def test_fitted_shell_is_cached_once_not_queried_per_frame(self):
        entry = SESSION.split("private bool TryEnter()",1)[1].split(
            "private AarisVehicleMotor FindReachableCarOnTap()",1)[0]
        chase = SESSION.split("private void FollowCarCamera()",1)[1].split(
            "// VehicleSession is the authoritative",1)[0]
        self.assertIn("BoxCollider shell = car.GetComponent<BoxCollider>();", entry)
        self.assertIn("shell.center.y + shell.size.y * .62f", entry)
        self.assertIn("chaseBodyLength = Mathf.Clamp(shell.size.z, 3.0f, 5.3f)",entry)
        self.assertIn("chaseAimHeight = Mathf.Clamp(chaseAimHeight, .68f, 1.65f)", entry)
        self.assertIn("if (shell != null &&", entry)
        self.assertIn("float.IsNaN(shell.size.sqrMagnitude)", entry)
        self.assertIn("float.IsInfinity(shell.size.sqrMagnitude)", entry)
        self.assertIn("chaseBodyLength - 4.20f", chase)
        self.assertIn("chaseAimHeight + .12f * speed01", chase)
        self.assertIn("Vector3.ProjectOnPlane(", chase)
        self.assertIn("Physics.SphereCastNonAlloc(", chase)
        self.assertNotIn("GetComponent<BoxCollider>", chase)
        self.assertNotIn("Renderer.bounds", chase)
        self.assertNotIn("GetComponentsInChildren(", chase)
        self.assertNotIn("new GameObject", chase)
        self.assertNotIn("new Material", chase)

    def test_long_vehicle_is_frmed_farther_without_becoming_tiny(self):
        # Representative fitted shells measured in native Unity; note the
        # shell is a chassis-only solid and is smaller than visual overhangs.
        cars = {
            "GT": (4.211,.15,1.0),
            "Sedan": (4.216,.505,1.27),
            "SUV": (4.512,.47,1.64),
            "Pickup": (5.095,.47,1.65),
            "Buggy": (3.210,.16,1.1),
        }
        for speed in (0, .3, .65, 1):
            values={name:frame(*p,speed) for name,p in cars.items()}
            for aim,height,dist in values.values():
                self.assertTrue(all(math.isfinite(x) for x in (aim,height,dist)))
                self.assertGreaterEqual(aim,.68)
                self.assertLess(aim,1.8)
                self.assertGreater(height,2.5)
                self.assertLess(height,3.2)
                self.assertGreater(dist,5.9)
                self.assertLess(dist,7.9)
            self.assertGreater(values["Pickup"][2], values["GT"][2])
            self.assertGreater(values["GT"][2], values["Buggy"][2])
            # GT should aim much lower than a roof-high SUV.
            self.assertLess(values["GT"][0], values["SUV"][0]-.25)

    def test_ground_collision_camera_rules_unchanged(self):
        camera = SESSION.split("private void FollowCarCamera()",1)[1].split(
            "// VehicleSession is the authoritative",1)[0]
        for exact in (
            "Physics.SphereCastNonAlloc(",
            "Physics.SphereCastAll(",
            "cameraVelocity = Vector3.zero;",
            "hit.collider.transform.IsChildOf(vehicle)",
            "RaycastHit[] hits = cameraObstacles",
            "Vector3.ProjectOnPlane(", "Mathf.Clamp(Time.deltaTime, 0f, .15f)",
            "followCamera.fieldOfView = fovError <= 0.1f",
        ):
            self.assertIn(exact,camera)

if __name__ == "__main__":
    unittest.main()
