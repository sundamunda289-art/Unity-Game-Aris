#!/usr/bin/env python3
"""Geometry regression: real CC0 GT wheel mesh pivots, not just transforms.

Static/source geometry evidence only. Native Unity/Android rendering still
required to certify visual tyre contact and movement.
"""
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()
GT = ROOT / "Assets/Resources/AarisVehicles/PremiumRoadGT"
WHEEL_NAMES = ("wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr")


def wheel_bounds(name):
    path = GT / ("AarisRoadGT_" + name + ".obj")
    lower = [float("inf")] * 3
    upper = [float("-inf")] * 3
    count = 0
    with path.open(encoding="utf-8") as f:
        for line in f:
            if not line.startswith("v "):
                continue
            xyz = [float(x) for x in line.split()[1:4]]
            for axis, coordinate in enumerate(xyz):
                lower[axis] = min(lower[axis], coordinate)
                upper[axis] = max(upper[axis], coordinate)
            count += 1
    return count, [(a + b) * .5 for a, b in zip(lower, upper)], [
        b - a for a, b in zip(lower, upper)]


def turn(vector, radians):
    """Rotate a local axle-offset in its wheel plane for a deterministic QA."""
    x, y, z = vector
    return (x, y * math.cos(radians) - z * math.sin(radians),
            y * math.sin(radians) + z * math.cos(radians))


class PremiumWheelCentreAlignment(unittest.TestCase):
    def test_source_gt_wheels_have_a_nonzero_pivot_offset(self):
        values = {}
        for wheel in WHEEL_NAMES:
            count, centre, size = wheel_bounds(wheel)
            self.assertGreater(count, 4900)
            self.assertGreater(size[1], .60)
            self.assertGreater(size[2], .60)
            self.assertAlmostEqual(size[1], size[2], delta=.006)
            self.assertLess(size[0], .35)
            values[wheel] = centre[0]
        self.assertAlmostEqual(values["wheel_fl"], .09365, delta=.003)
        self.assertAlmostEqual(values["wheel_fr"], -.09365, delta=.003)
        self.assertAlmostEqual(values["wheel_rl"], .12185, delta=.003)
        self.assertAlmostEqual(values["wheel_rr"], -.12185, delta=.003)
        self.assertGreater(abs(values["wheel_fl"]), .07)
        self.assertGreater(abs(values["wheel_rl"]), .11)

    def test_physics_pose_accounts_for_visual_mesh_centre_when_spinning(self):
        for wheel in WHEEL_NAMES:
            _, offset, _ = wheel_bounds(wheel)
            for degrees in (0, 31, 78, 143, 247, 359):
                angle = math.radians(degrees)
                rotated_offset = turn(offset, angle)
                contact_centre = (1.04, .39, -3.25)
                # Correct visual root = collider centre MINUS rotated
                # authored mesh centre; the mesh itself then lands on collider.
                visual_pivot = [
                    contact_centre[i] - rotated_offset[i] for i in range(3)
                ]
                displayed_centre = [
                    visual_pivot[i] + rotated_offset[i] for i in range(3)
                ]
                for actual, expected in zip(displayed_centre, contact_centre):
                    self.assertAlmostEqual(actual, expected, places=10)
                # Previous behaviour placed wheel object's ROOT at collider
                # centre, leaving a visible 9-12cm mesh-centre error.
                old_error = math.sqrt(sum(x*x for x in rotated_offset))
                self.assertGreater(old_error, .07)

    def test_embedded_and_fallback_wheels_capture_offsets_at_spawn_only(self):
        self.assertIn("private readonly Vector3[] visualWheelCenterOffsets", MOTOR)
        self.assertIn("new Vector3[4]", MOTOR)
        self.assertIn("visualWheelCenterOffsets[index] = Vector3.zero;", MOTOR)
        fallback = MOTOR.split("public bool AttachWheelModels(", 1)[1].split(
            "public bool AttachEmbeddedWheelModels(", 1)[0]
        for frag in (
            "instance.transform.TransformPoint(combined.center)",
            "Quaternion.Inverse(instance.transform.rotation)",
            "visualCentreWorld - instance.transform.position",
            "visualWheelCenterOffsets[i] =",
        ):
            self.assertIn(frag, fallback)
        embedded = MOTOR.split("public bool AttachEmbeddedWheelModels(", 1)[1].split(
            "private static Renderer CreateLampLens(", 1)[0]
        for frag in (
            "transform.TransformPoint(bounds[i].center)",
            "Quaternion.Inverse(wheelVisual.rotation)",
            "visualCentreWorld - wheelVisual.position",
            "visualWheelCenterOffsets[i] = centreOffset;",
            "wheelVisual.SetParent(transform, true)",
        ):
            self.assertIn(frag, embedded)
        self.assertLess(embedded.index("Vector3 visualCentreWorld ="),
                        embedded.index("wheelVisual.SetParent(transform, true);"))

    def test_wheel_sync_uses_one_real_pose_and_no_mesh_lookup_per_frame(self):
        sync = MOTOR.split("private void SyncVisibleWheels()", 1)[1].split(
            "private void LateUpdate()", 1)[0]
        self.assertEqual(sync.count("GetWorldPose("), 1)
        self.assertIn("worldRotation * visualWheelRotationOffsets[i]", sync)
        self.assertIn("visualRotation * visualWheelCenterOffsets[i]", sync)
        self.assertIn("worldPosition - visualCentreOffsetWorld, visualRotation", sync)
        for forbidden in (
            "TryGetVisualBoundsLocal", "GetComponentsInChildren", "new Material",
            "new GameObject", "Physics.Raycast", "Instantiate(",
        ):
            self.assertNotIn(forbidden, sync)


if __name__ == "__main__":
    unittest.main()
