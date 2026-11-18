#!/usr/bin/env python3
"""Safe aero/body motion when one car axle loses road contact.

Physics geometry contracts; does not claim native WheelCollider Play Mode.
"""
import itertools
import math
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()


def axle_mask(contacts):
    """Reference from four existing WheelHit contact booleans."""
    fl, fr, rl, rr = contacts
    mask = 0
    if fl or fr:
        mask |= 1
    if rl or rr:
        mask |= 2
    return mask


def may_apply_aero(speed, contacts):
    return speed > 6 and sum(contacts) >= 2 and axle_mask(contacts) == 3


class AxleSupportedPhysics(unittest.TestCase):
    def test_single_supported_axle_never_gets_suction_at_high_speed(self):
        cases = [
            ((True, True, False, False), "front-only"),
            ((False, False, True, True), "rear-only"),
            ((False, False, False, False), "airborne"),
            ((True, False, False, False), "one-wheel front"),
            ((False, False, False, True), "one-wheel rear"),
        ]
        for contact, name in cases:
            with self.subTest(name=name):
                for speed in (6.01, 12, 24, 30):
                    self.assertFalse(may_apply_aero(speed, contact))
        # Straddling curb, one front and one rear support: allowed.
        for contact in (
            (True, False, True, False),
            (False, True, False, True),
            (True, False, False, True),
            (False, True, True, False),
            (True, True, True, False),
            (True, False, True, True),
            (True, True, True, True),
        ):
            self.assertTrue(may_apply_aero(24, contact))
            self.assertFalse(may_apply_aero(6, contact))

    def test_every_four_wheel_contact_pattern_is_mask_consistent(self):
        for contacts in itertools.product((False, True), repeat=4):
            mask = axle_mask(contacts)
            self.assertIn(mask, (0, 1, 2, 3))
            self.assertEqual(mask & 1 != 0, any(contacts[:2]))
            self.assertEqual(mask & 2 != 0, any(contacts[2:]))
            self.assertEqual(
                may_apply_aero(15, contacts),
                any(contacts[:2]) and any(contacts[2:]),
            )

    def test_contact_mask_derived_from_same_four_wheelhits_not_new_queries(self):
        self.assertIn("private int groundedAxleMask;", MOTOR)
        fixed = MOTOR.split("private void FixedUpdate()", 1)[1].split(
            "private void ApplyAckermannSteering(", 1)[0]
        antiroll = MOTOR.split("private void ApplyAntiRoll(", 1)[1].split(
            "private void OnDisable()", 1)[0]
        self.assertIn("groundedAxleMask = 0;", fixed)
        self.assertLess(fixed.index("groundedAxleMask = 0;"),
                        fixed.index("ApplyAntiRoll(0, 1);"))
        self.assertLess(fixed.index("ApplyAntiRoll(0, 1);"),
                        fixed.index("ApplyAntiRoll(2, 3);"))
        self.assertIn("if (leftGrounded || rightGrounded)", antiroll)
        self.assertIn("groundedAxleMask |= leftIndex == 0 ? 1 : 2;", antiroll)
        self.assertEqual(antiroll.count("GetGroundHit(out WheelHit"), 2)
        self.assertNotIn("Physics.Raycast", antiroll)
        self.assertNotIn("new GameObject", antiroll)
        self.assertNotIn("new Material(", antiroll)

    def test_both_axles_guard_downforce_and_cosmetic_body_motion(self):
        fixed = MOTOR.split("private void FixedUpdate()", 1)[1].split(
            "private void ApplyAckermannSteering(", 1)[0]
        self.assertIn(
            "if (planarSpeed > 6f && groundedWheelCount >= 2 &&", fixed
        )
        self.assertIn("groundedAxleMask == 3", fixed)
        self.assertIn("Mathf.Min(3.5f, .007f * planarSpeed * planarSpeed)", fixed)
        motion = MOTOR.split("private void SampleBodyDynamics()", 1)[1].split(
            "private void UpdateVisualBodyDynamics()", 1)[0]
        self.assertIn("grounded < 2 || groundedAxleMask != 3 ||", motion)
        self.assertIn("visualPitchTarget = visualRollTarget = visualJounceTarget = 0f;", motion)
        self.assertNotIn("GetGroundHit(", motion)
        self.assertNotIn("Physics.Raycast", motion)

    def test_added_aero_load_stays_bounded_in_supported_cases(self):
        for mass in (850, 1250, 1800, 2300):
            for speed in (6.01, 12, 24, 28, 40):
                force = mass * min(3.5, .007 * speed * speed)
                self.assertGreaterEqual(force, 0)
                self.assertLessEqual(force / mass, 3.5 + 1e-9)
                self.assertTrue(math.isfinite(force))


if __name__ == "__main__":
    unittest.main()
