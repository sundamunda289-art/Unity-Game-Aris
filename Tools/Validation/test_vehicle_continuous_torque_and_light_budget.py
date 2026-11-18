#!/usr/bin/env python3
"""Source and numerical checks for continuous car torque and lazy lighting."""
import math
import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
SOURCE=(ROOT/"Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()

def torque(speed):
    x=abs(speed)
    if x>=21: return 580.
    if x<7: lo,hi,t=1500.,1180.,x/7
    elif x<14: lo,hi,t=1180.,850.,(x-7)/7
    else: lo,hi,t=850.,580.,(x-14)/7
    t=max(0.,min(1.,t))
    return lo+(hi-lo)*(t*t*(3-2*t))

class MobileVehicleBudget(unittest.TestCase):
    def test_curve_wired_to_physics_without_extra_objects(self):
        helper=SOURCE.split("private static float ContinuousDriveTorque(",1)[1].split("private void FixedUpdate()",1)[0]
        for value in ('speedAbs < 7f','speedAbs < 14f','speedAbs < 21f',
                      'Mathf.SmoothStep(1500f, 1180f',
                      'Mathf.SmoothStep(1180f, 850f',
                      'Mathf.SmoothStep(850f, 580f','return 580f;'):
            self.assertIn(value,helper)
        fixed=SOURCE.split("private void FixedUpdate()",1)[1]
        self.assertIn("float baseTorque = ContinuousDriveTorque(speedAbs);",fixed)
        self.assertIn("ApplyWheelForces(drive, brake);",fixed)
        self.assertNotIn("body.linearVelocity =",fixed)
        self.assertNotIn("new Material(",fixed)

    def test_monotone_continuous_torque_bidirectional_and_finite(self):
        for speed,value in ((0,1500),(7,1180),(14,850),(21,580),(28,580)):
            self.assertAlmostEqual(torque(speed),value)
            self.assertAlmostEqual(torque(-speed),value)
        last=torque(0)
        for i in range(1,3001):
            current=torque(i/100)
            self.assertTrue(math.isfinite(current))
            self.assertLessEqual(current,last+1e-6)
            self.assertLessEqual(last-current,1.1)
            last=current
        for x in (7,14,21):
            self.assertLess(abs(torque(x-1e-5)-torque(x+1e-5)),.002)

    def test_no_spotlight_created_for_parked_car(self):
        cfg=SOURCE.split("public void ConfigureExteriorLights(",1)[1].split("private void EnsureRoadHeadlight()",1)[0]
        lazy=SOURCE.split("private void EnsureRoadHeadlight()",1)[1].split("private void RefreshExteriorLights()",1)[0]
        enter=SOURCE.split("public bool TryTakeDriver(",1)[1].split("public void SetInput(",1)[0]
        self.assertNotIn("AddComponent<Light>()",cfg)
        self.assertEqual(lazy.count("AddComponent<Light>()"),1)
        self.assertIn('new GameObject("Player Car Headlight Beam")',lazy)
        self.assertIn("roadHeadlight != null",lazy)
        self.assertIn("LightShadows.None",lazy)
        self.assertIn("frontLamps[0].transform.parent",lazy)
        self.assertIn("EnsureRoadHeadlight();",enter)
        self.assertIn("RefreshExteriorLights();",enter)
        lights=SOURCE.split("private void RefreshExteriorLights()",1)[1]
        self.assertIn("roadHeadlight.enabled = roadBeam;",lights)
        self.assertIn("bool roadBeam = driving && NeedsRoadBeam();",lights)
        self.assertIn("(roadBeam ? 8 : 0)",lights)
        self.assertNotIn("EnsureRoadHeadlight();",SOURCE.split("private void FixedUpdate()",1)[1])

if __name__=="__main__": unittest.main()
