#!/usr/bin/env python3
"""Source/asset regression tests. Does NOT claim a Unity/Android render."""
from pathlib import Path
import json, re, unittest

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / 'Assets/AarisWorld/Scripts/AarisVehicleWorld.cs').read_text()
MOTOR = (ROOT / 'Assets/AarisWorld/Scripts/AarisVehicleMotor.cs').read_text()
CATALOG = json.loads((ROOT / 'Assets/Resources/AarisVehicles/vehicle_catalog.json').read_text())


class PremiumVehicleFleet(unittest.TestCase):
    def test_distinct_gt_uses_existing_licensed_body(self):
        vehicles = {x['id']: x for x in CATALOG['vehicles']}
        self.assertEqual(len(vehicles), 8)
        gt = vehicles['GT']
        self.assertEqual(gt['authoredModelResource'],
                         'AarisVehicles/PremiumRoadGT/AarisRoadGT')
        self.assertEqual(gt['fallbackAuthoredModelResource'],
                         'AarisVehicles/Authored/QuaterniusSports')
        self.assertEqual(gt['modelResource'], 'AarisVehicles/Models/sedan-sports')
        self.assertEqual(gt['wheelResource'], 'AarisVehicles/Models/wheel-racing')
        self.assertEqual(gt['drivetrain'], 'RWD')
        self.assertTrue(0.8 < gt['suspensionTravelScale'] < 1)
        asset = ROOT / 'Assets/Resources/AarisVehicles/Authored/QuaterniusSports.obj'
        self.assertTrue(asset.exists())
        self.assertTrue(Path(str(asset)+'.meta').exists())
        self.assertIn('usemtl AarisPaint', asset.read_text())
        premium = ROOT / 'Assets/Resources/AarisVehicles/PremiumRoadGT'
        prefab = premium / 'AarisRoadGT.prefab'
        self.assertTrue(prefab.exists())
        self.assertTrue(Path(str(prefab)+'.meta').exists())
        for component in ['body','wheel_fl','wheel_fr','wheel_rl','wheel_rr']:
            part=premium / ('AarisRoadGT_'+component+'.obj')
            self.assertTrue(part.exists(),part)
            self.assertTrue(Path(str(part)+'.meta').exists())
        self.assertIn('AarisPaint_RoadGT',
                      (premium / 'AarisRoadGT.mtl').read_text())
        self.assertGreaterEqual(gt['weight'] + vehicles['Sports']['weight'], 8)
        self.assertLess(vehicles['Sedan']['weight'],
                        gt['weight'] + vehicles['Sports']['weight'])

    def test_premium_palette_readable_and_not_neon(self):
        section = WORLD.split('private static readonly Color[] BodyPaintColours =',1)[1].split(
            'private static int StablePaintVariant(',1)[0]
        values = re.findall(r'new Color\(([^)]*)\)', section)
        self.assertEqual(len(values), 8)
        for raw in values:
            rgb = [float(x.strip().rstrip('f')) for x in raw.split(',')[:3]]
            self.assertGreater(max(rgb), .35)
            self.assertLessEqual(max(rgb), .92)
            self.assertGreater(min(rgb), .10)

    def test_untextured_body_takes_colour_not_imported_red(self):
        code = WORLD.split('internal static Color SelectPaintTint(',1)[1].split(
            'private static void PaintVerifiedBodySlots(',1)[0]
        self.assertIn('baseMap == Texture2D.whiteTexture', code)
        self.assertIn('? paint : Color.Lerp(original, paint, .85f)', code)
        self.assertIn('return new Color(dedicated.r, dedicated.g, dedicated.b, original.a);', code)
        self.assertIn('Color.Lerp(Color.white, paint, .30f)', code)
        self.assertIn('Color.Lerp(Color.white, paint, .45f)', code)

    def test_wear_and_spawn_use_identical_colour(self):
        self.assertIn('Color tint = SelectPaintTint(', WORLD)
        self.assertIn('Color fresh = AarisVehicleWorld.SelectPaintTint(', MOTOR)
        self.assertIn('renderer.GetPropertyBlock(block, slot)', WORLD)
        self.assertIn('renderer.SetPropertyBlock(block, slot)', WORLD)
        self.assertIn('entry.renderer.GetPropertyBlock(bodyPaintBlock, entry.index)', MOTOR)
        self.assertNotIn('new Material(', WORLD.split(
            'private static void PaintVerifiedBodySlots(',1)[1].split(
            'private void FallBackToVerifiedModel(',1)[0])

if __name__ == '__main__':
    unittest.main(verbosity=2)
