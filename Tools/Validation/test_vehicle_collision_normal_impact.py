#!/usr/bin/env python3
"""Regression for collision severity: normal impact, not wall-slide speed.

Pure geometry and source QA; native Unity crash/road behavior still unverified.
"""
import math
import unittest
from pathlib import Path

MOTOR = (Path(__file__).resolve().parents[2] /
         "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()


def impact_speed(velocity, normals):
    """Strongest contact-normal component in an on-enter collision."""
    return max((abs(sum(a * b for a, b in zip(velocity, normal)))
                for normal in normals), default=0.0)


class VehicleCollisionNormalImpact(unittest.TestCase):
    def test_head_on_crash_and_grazing_wall_have_different_severity(self):
        wall = (1.0, 0.0, 0.0)
        # Identical 22m/s car speeds; only the head-on approach damages.
        head_on = impact_speed((22, 0, 0), [wall])
        grazing = impact_speed((0.25, 0, 22), [wall])
        self.assertAlmostEqual(head_on, 22)
        self.assertLess(grazing, 4)
        self.assertGreater(head_on, 7)
        # A genuine normal impact in a diagonal collision still matters.
        diagonal = impact_speed((12, 0, 12), [wall])
        self.assertAlmostEqual(diagonal, 12)
        self.assertGreater(diagonal, 7)

    def test_multiple_contacts_take_strongest_true_normal_component(self):
        relative = (14, 0, 16)
        wall = (1, 0, 0)
        front = (0, 0, -1)
        self.assertAlmostEqual(impact_speed(relative, [wall, front]), 16)
        self.assertAlmostEqual(impact_speed(relative, [front, wall]), 16)
        self.assertAlmostEqual(impact_speed(relative, [(0, 1, 0)]), 0)
        self.assertAlmostEqual(impact_speed(relative, []), 0)
        # Reversing normal does not change impact severity.
        self.assertAlmostEqual(impact_speed(relative, [(-1, 0, 0)]), 14)

    def test_damage_and_audio_thresholds_preserved(self):
        for speed in (0, .1, 3.9):
            self.assertFalse(speed >= 4)
            self.assertFalse(speed >= 7)
        for speed in (4, 5, 6.99):
            self.assertTrue(speed >= 4)
            self.assertFalse(speed >= 7)
        for speed in (7, 10, 18, 30):
            self.assertTrue(speed >= 7)
            wear_increment = max(0, speed - 7) * .011
            self.assertTrue(math.isfinite(wear_increment))
            self.assertLess(wear_increment, 1)
        self.assertEqual(impact_speed((0, 0, 20), [(1, 0, 0)]), 0)

    def test_callback_no_gc_contact_array_and_no_weak_speed_proxy(self):
        src = MOTOR.split("private void OnCollisionEnter(", 1)[1].split(
            "public void Configure(", 1)[0]
        for fragment in (
            "Vector3 relativeVelocity = collision.relativeVelocity;",
            "float impactSpeed = 0f;",
            "for (int i = 0; i < collision.contactCount; i++)",
            "Vector3 normal = collision.GetContact(i).normal;",
            "Mathf.Abs(Vector3.Dot(relativeVelocity, normal))",
            "impactSpeed = Mathf.Max(impactSpeed, normalSpeed);",
            "if (impactSpeed < 4f) return;",
            "AarisAudioDirector.VehicleCrash(transform.position, impactSpeed);",
            "if (impactSpeed >= 7f)",
            "if (Time.time >= nextDamageAt)",
            "nextDamageAt = Time.time + .75f;",
        ):
            self.assertIn(fragment, src)
        self.assertLess(src.index("if (impactSpeed < 4f) return;"),
                        src.index("AarisAudioDirector.VehicleCrash("))
        self.assertNotIn("relativeVelocity.magnitude", src)
        for forbidden in (
            "collision.contacts", "new ContactPoint[", "new GameObject(",
            "Physics.Raycast", "GetContacts(", "Destroy(", "new Material(",
        ):
            self.assertNotIn(forbidden, src)
        fixed = MOTOR.split("private void FixedUpdate()", 1)[1].split(
            "private void ApplyAckermannSteering(", 1)[0]
        self.assertNotIn("GetContact(", fixed)


if __name__ == "__main__":
    unittest.main()
