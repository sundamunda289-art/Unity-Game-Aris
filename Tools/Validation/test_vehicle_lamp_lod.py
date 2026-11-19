#!/usr/bin/env python3
"""Vehicle decorative lamp LOD contracts; source checks, not Unity FPS proof."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()


def hidden_at(distance, previously_hidden, occupied=False):
    near, far = 52.0, 68.0
    threshold = near if previously_hidden else far
    return not occupied and distance * distance > threshold * threshold


class VehicleLampLod(unittest.TestCase):
    def test_all_ten_overlays_use_one_lod_mount(self):
        method = MOTOR.split("public void ConfigureExteriorLights(", 1)[1].split(
            "private void UpdateLampDetailLod()", 1)[0]
        self.assertIn('lampDetailRoot = new GameObject("Mobile Vehicle Lamp Detail");', method)
        self.assertIn("lampDetailRoot.transform.SetParent(lampChassis, false);", method)
        self.assertEqual(method.count("CreateLampLens(lampMount,"), 5)
        self.assertNotIn("CreateLampLens(lampChassis,", method)
        self.assertIn("reversingLamps[side].enabled = false;", method)
        self.assertIn("lastLampState = -1;", method)

    def test_hysteresis_and_driver_safety(self):
        self.assertIn("LampDetailFarDistanceSq = 68f * 68f", MOTOR)
        self.assertIn("LampDetailNearDistanceSq = 52f * 52f", MOTOR)
        self.assertFalse(hidden_at(60, False))
        self.assertTrue(hidden_at(69, False))
        self.assertTrue(hidden_at(60, True))
        self.assertFalse(hidden_at(50, True))
        self.assertFalse(hidden_at(100, True, occupied=True))
        lod = MOTOR.split("private void UpdateLampDetailLod()", 1)[1].split(
            "private void EnsureRoadHeadlight()", 1)[0]
        for guard in ("driver == null", "Time.unscaledTime + .4f",
                      "lampDetailRoot.SetActive(!hide);", ".sqrMagnitude"):
            self.assertIn(guard, lod)
        for forbidden in ("new Material(", "new Mesh(", "GetComponent<",
                          "Camera.main", "GetWorldPose(", "Mathf.Sqrt("):
            self.assertNotIn(forbidden, lod)

    def test_distant_lod_does_not_touch_physics_or_driver_controls(self):
        late = MOTOR.split("private void LateUpdate()", 1)[1].split(
            "private void OnEnable()", 1)[0]
        self.assertLess(late.index("UpdateLampDetailLod();"),
                        late.index("if (parkedSleeping && sleepingWheelPoseCurrent"))
        setup = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("reverseLampMaterial, sharedLampLensMesh, lampBezelMaterial,\n                player);", setup)
        lod = MOTOR.split("private void UpdateLampDetailLod()", 1)[1].split(
            "private void EnsureRoadHeadlight()", 1)[0]
        self.assertNotIn("WheelCollider", lod)
        self.assertNotIn("body.Sleep", lod)
        self.assertNotIn("gameObject.SetActive", lod)


if __name__ == "__main__":
    unittest.main()
