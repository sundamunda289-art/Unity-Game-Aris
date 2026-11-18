#!/usr/bin/env python3
"""Fail-closed collision/wheel-axle geometry regression for realistic cars.

A source/geometry contract; actual Unity prefab/Android Play Mode still required.
"""
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()


def valid_axles(fl, fr, rl, rr):
    """Independent numeric model of broad authored-axle validation limits."""
    front_track = fr[0] - fl[0]
    rear_track = rr[0] - rl[0]
    wheelbase = (fl[2] + fr[2] - rl[2] - rr[2]) * .5
    if not (1.02 <= front_track <= 2.40 and
            1.02 <= rear_track <= 2.40 and
            1.55 <= wheelbase <= 4.65):
        return False
    return (abs(fl[2] - fr[2]) <= .30 and
            abs(rl[2] - rr[2]) <= .30 and
            abs(fl[1] - fr[1]) <= .22 and
            abs(rl[1] - rr[1]) <= .22 and
            abs((fl[2] - rl[2]) - (fr[2] - rr[2])) <= .42 and
            abs((fl[0] + fr[0]) * .5) <= .60 and
            abs((rl[0] + rr[0]) * .5) <= .60)


class AxleRoadFailClosed(unittest.TestCase):
    def test_bad_import_cannot_succeed_after_ground_alignment_failure(self):
        build = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        guard = build.index("!AlignEmbeddedTyresToRoad(")
        fallback = build.index('FallBackToVerifiedModel(typeIndex, "tyre ground alignment")')
        destroy = build.index("Destroy(root);", fallback)
        fail = build.index("return false;", destroy)
        physics = build.index("var motor = root.AddComponent<AarisVehicleMotor>()")
        self.assertLess(guard, fallback)
        self.assertLess(fallback, destroy)
        self.assertLess(destroy, fail)
        self.assertLess(fail, physics)
        self.assertIn("if (record.type.useEmbeddedAuthoredWheels &&", build)
        self.assertIn("if (!TryGetVisualBoundsLocal(root.transform, out bounds))", build)

    def test_all_four_road_tyres_still_have_ground_spread_guard(self):
        helper = WORLD.split("private static bool AlignEmbeddedTyresToRoad(", 1)[1].split(
            "private static bool HasEmbeddedWheelSet(", 1)[0]
        for required in (
            '"wheel_fl"', '"wheel_fr"', '"wheel_rl"', '"wheel_rr"',
            "TryGetVisualBoundsLocal(", "tyreBounds.min.y",
            "maximumBottom - minimumBottom > .22f",
            "diameter < .40f || diameter > 1.06f",
            "Mathf.Abs(offset) > .95f",
            "const float TyreBottomInChassis = -.71f + .10f",
        ):
            self.assertIn(required, helper)

    def test_axle_shape_checked_before_mutating_physics_and_mesh(self):
        embedded = MOTOR.split("public bool AttachEmbeddedWheelModels(", 1)[1].split(
            "// Flush chamfered lamp polygons", 1)[0]
        self.assertIn("if (!AreAuthoredWheelAxlesPlausible(bounds)) return false;", embedded)
        self.assertLess(embedded.index("AreAuthoredWheelAxlesPlausible(bounds)"),
                        embedded.index("wheelVisual.SetParent(transform, true)"))
        self.assertLess(embedded.index("AreAuthoredWheelAxlesPlausible(bounds)"),
                        embedded.index("wheels[i].transform.localPosition = bounds[i].center"))
        helper = MOTOR.split("private static bool AreAuthoredWheelAxlesPlausible(", 1)[1].split(
            "// Flush chamfered lamp polygons", 1)[0]
        self.assertIn("frontTrack = fr.x - fl.x", helper)
        self.assertIn("rearTrack = rr.x - rl.x", helper)
        self.assertIn("wheelbase = frontZ - rearZ", helper)
        self.assertIn("wheelbase < 1.55f || wheelbase > 4.65f", helper)
        self.assertIn("Mathf.Abs(fl.y - fr.y) > .22f", helper)
        self.assertIn("Mathf.Abs(rl.y - rr.y) > .22f", helper)
        for forbidden in ("new GameObject", "Physics.Raycast", "new Material("):
            self.assertNotIn(forbidden, helper)

    def test_unequal_tyre_diameters_fail_before_wheel_placement(self):
        helper = MOTOR.split("private static bool AreAuthoredWheelAxlesPlausible(", 1)[1].split(
            "// Flush chamfered lamp polygons", 1)[0]
        for fragment in (
            "float frontLeftDiameter = Mathf.Max(tyres[0].size.y, tyres[0].size.z);",
            "float frontRightDiameter = Mathf.Max(tyres[1].size.y, tyres[1].size.z);",
            "float rearLeftDiameter = Mathf.Max(tyres[2].size.y, tyres[2].size.z);",
            "float rearRightDiameter = Mathf.Max(tyres[3].size.y, tyres[3].size.z);",
            "Mathf.Max(frontLeftDiameter, frontRightDiameter) * .12f",
            "Mathf.Max(rearLeftDiameter, rearRightDiameter) * .12f",
            "Mathf.Abs(frontLeftDiameter - frontRightDiameter) > frontTolerance",
            "Mathf.Abs(rearLeftDiameter - rearRightDiameter) > rearTolerance",
        ):
            self.assertIn(fragment, helper)

        def matching(left, right):
            return abs(left - right) <= max(.07, .12 * max(left, right))
        # Genuine Premium GT's ~686 mm front and ~722 mm rear tyre pairs.
        for size in (.686, .722):
            self.assertTrue(matching(size, size))
        for left, right in ((.65, .95), (.99, .60), (.41, .72), (.80, 1.04)):
            self.assertFalse(matching(left, right))
        for left, right in ((.68, .74), (.78, .85), (.93, 1.00)):
            self.assertTrue(matching(left, right))
        embedded = MOTOR.split("public bool AttachEmbeddedWheelModels(", 1)[1].split(
            "private static bool AreAuthoredWheelAxlesPlausible(", 1)[0]
        self.assertLess(
            embedded.index("if (!AreAuthoredWheelAxlesPlausible(bounds)) return false;"),
            embedded.index("wheels[i].radius ="))

    def test_authentic_vehicle_geometry_accepted_and_corrupt_axes_rejected(self):
        for track, span, y in (
            (1.26, 1.75, -.20),  # short buggy
            (1.56, 2.38, -.23),  # hatch
            (1.70, 2.67, -.28),  # sedan
            (1.72, 2.90, -.30),  # SUV
            (1.80, 2.54, -.27),  # GT
            (1.90, 3.45, -.24),  # utility
        ):
            t = track / 2
            z = span / 2
            self.assertTrue(valid_axles((-t, y, z), (t, y, z),
                                        (-t, y, -z), (t, y, -z)))
        gt = ((-.90095, -.26, 1.27), (.90095, -.26, 1.27),
              (-.90095, -.27, -1.27), (.90095, -.27, -1.27))
        self.assertTrue(valid_axles(*gt))
        self.assertFalse(valid_axles(gt[1], gt[0], gt[2], gt[3]))
        self.assertFalse(valid_axles(gt[2], gt[3], gt[0], gt[1]))
        self.assertFalse(valid_axles((-1, 0, .4), (1, 0, .4),
                                     (-1, 0, -.4), (1, 0, -.4)))
        self.assertFalse(valid_axles((-.9, -.4, 1.3), (.9, .2, 1.3),
                                     (-.9, -.4, -1.3), (.9, -.4, -1.3)))
        self.assertFalse(valid_axles((-.9, 0, 1.4), (.9, 0, .7),
                                     (-.9, 0, -1.4), (.9, 0, -1.4)))


if __name__ == "__main__":
    unittest.main()
