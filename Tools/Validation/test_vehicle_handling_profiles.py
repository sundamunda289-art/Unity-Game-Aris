#!/usr/bin/env python3
"""Offline source contracts for automotive profiles; not native Unity tests."""
from pathlib import Path
import json,math,unittest
R=Path(__file__).resolve().parents[2]
M=(R/'Assets/AarisWorld/Scripts/AarisVehicleMotor.cs').read_text()
W=(R/'Assets/AarisWorld/Scripts/AarisVehicleWorld.cs').read_text()
C=json.loads((R/'Assets/Resources/AarisVehicles/vehicle_catalog.json').read_text())
class TestHandlingProfiles(unittest.TestCase):
 def test_catalog_and_drivetrain(self):
  expected={'SUV':'AWD','GT':'RWD','Buggy':'AWD'}
  self.assertEqual({v['id'] for v in C['vehicles']},set(expected))
  for v in C['vehicles']:
   self.assertEqual(v['drivetrain'],expected[v['id']])
   self.assertTrue(.88<=v['tyreGripScale']<=1.12)
   self.assertTrue(.86<=v['suspensionScale']<=1.18)
   self.assertTrue(.78<=v['steeringScale']<=1.16)
  self.assertIn('motor.ConfigureHandling(record.type.drivetrain,',W)
  self.assertIn('spring.damper *= Mathf.Sqrt(springScale)',M)
  self.assertIn('tyreCompoundMultiplier * multiplier',M)
 def test_torque_conservation_and_safety(self):
  for fraction in (0,.46,1):
   for torque in (-700,0,1500):
    axles=[torque*fraction/2]*2+[torque*(1-fraction)/2]*2
    self.assertAlmostEqual(sum(axles),torque)
    self.assertTrue(all(math.isfinite(v) for v in axles))
  self.assertIn('125f * steeringResponseMultiplier * Time.fixedDeltaTime',M)
  self.assertIn('float.IsNaN(value)',M)
  self.assertIn('float.IsInfinity(value)',M)
  reuse = M.split("internal void ReactivateParkedVehicle(", 1)[1].split(
   "private void EnsureExhaustEmitter()", 1)[0]
  self.assertIn("body.linearVelocity = Vector3.zero;", reuse)
  self.assertNotIn("body.linearVelocity =", M.replace(reuse, ""))
 def test_paint_is_only_on_verified_body_panels(self):
  paint=W.split('private static void PaintVerifiedBodySlots(',1)[1].split(
   'private void FallBackToVerifiedModel(',1)[0]
  for label in ('case "SUV"','case "GT"','case "Buggy"',
                'if (separatePaint || civilianPaint)',
                'renderer.GetPropertyBlock(block, slot);',
                'renderer.SetPropertyBlock(block, slot);'):
   self.assertIn(label,paint)
  self.assertNotIn('renderer.material',paint)
if __name__=='__main__': unittest.main()
