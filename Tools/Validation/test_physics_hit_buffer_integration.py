#!/usr/bin/env python3
"""Integration contracts; native collision behavior requires separate device QA."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / 'Assets/AarisWorld/Scripts'


class PhysicsScratchIntegration(unittest.TestCase):
    def test_original_consumers_keep_geometry_and_filters(self):
        gun = (SCRIPTS / 'AarisEquippedGun.cs').read_text()
        feet = (SCRIPTS / 'AarisCharacterVisual.cs').read_text()
        drop = (SCRIPTS / 'AarisOfflineDrop.cs').read_text()
        self.assertIn('Physics.DefaultRaycastLayers, ref rayHits, 256,', gun)
        self.assertIn('collider.transform.IsChildOf(owner)', gun)
        self.assertIn('if (candidates[i].distance >= minDistance) continue;', gun)
        self.assertIn('ref ankleSupportHits, 128, out RaycastHit[] hits', feet)
        self.assertIn('hit.normal.y < minWalkableNormalY', feet)
        self.assertIn('hit.collider.transform.IsChildOf(transform)', feet)
        self.assertIn('hit.distance >= closest', feet)
        self.assertIn('groundRay, rayLength,', drop)
        self.assertIn('Physics.DefaultRaycastLayers, ref groundHits, 256,', drop)
        self.assertIn('h.normal.y < 0.5f', drop)
        self.assertIn('h.point.y > position.y + 0.05f', drop)
        self.assertIn('0.17f, distance, Physics.DefaultRaycastLayers, ref cameraHits, 256,', drop)
        self.assertIn('hit.collider.transform.IsChildOf(plane.transform)', drop)
        self.assertIn('Mathf.Max(0.35f, hit.distance - 0.2f)', drop)

    def test_small_scenes_keep_original_initial_sizes(self):
        expected = {'AarisEquippedGun.cs': [('rayHits', 48)],
                    'AarisCharacterVisual.cs': [('ankleSupportHits', 12)],
                    'AarisOfflineDrop.cs': [('groundHits', 72), ('cameraHits', 64)]}
        for name, buffers in expected.items():
            code = (SCRIPTS / name).read_text()
            for field, size in buffers:
                self.assertIn(f'private RaycastHit[] {field} = new RaycastHit[{size}];', code)

    def test_no_global_pool_gameplay_shortcut_or_forced_collection(self):
        helper = (SCRIPTS / 'AarisPhysicsHitBuffer.cs').read_text()
        for token in ['Task.Run', 'Thread', 'GC.Collect', 'Array.Sort', 'Object.Find',
                      'Time.timeScale', 'QualitySettings', 'GetComponent', 'new GameObject']:
            self.assertNotIn(token, helper)
        self.assertEqual(helper.count('if (count >= retained.Length)'), 2)
        self.assertEqual(helper.count('count = hits.Length;'), 2)
        self.assertIn('Physics.RaycastAll(ray, distance,', helper)
        self.assertIn('Physics.SphereCastAll(ray, radius, distance,', helper)


if __name__ == '__main__':
    unittest.main(verbosity=2)
