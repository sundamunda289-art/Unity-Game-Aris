#!/usr/bin/env python3
"""Guard real visible car bounds before fitting chassis, wheelbase and lamps.

This is a static contract; native Unity screenshots and Android FPS remain required.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / 'Assets/AarisWorld/Scripts/AarisVehicleWorld.cs').read_text('utf-8')

class VisibleCarBounds(unittest.TestCase):
    def test_hidden_or_empty_imported_meshes_cannot_expand_fitted_body(self):
        section = WORLD.split('internal static bool TryGetVisualBoundsLocal(Transform localSpace,\n', 1)[1].split(
            'private static void AccumulateLocalBounds(', 1)[0]
        # Both MeshFilter+MeshRenderer and SkinnedMeshRenderer paths are visibility gated.
        self.assertIn('filter.sharedMesh.vertexCount == 0', section)
        self.assertIn('renderer == null || !renderer.enabled', section)
        self.assertIn('renderer.forceRenderingOff', section)
        self.assertIn('!renderer.gameObject.activeInHierarchy', section)
        self.assertIn('skinned.sharedMesh.vertexCount == 0 || !skinned.enabled', section)
        self.assertIn('skinned.forceRenderingOff', section)
        self.assertIn('!skinned.gameObject.activeInHierarchy', section)
        self.assertEqual(section.count('AccumulateLocalBounds('), 2)
        self.assertNotIn('renderer.bounds', section)

    def test_all_runtime_fit_points_share_visible_bounds(self):
        build = WORLD.split('private bool Build(', 1)[1].split('private void Update()', 1)[0]
        self.assertLess(build.index('if (!HasRenderableVehicleBody(renderers))'),
                        build.index('TryGetVisualBoundsLocal(root.transform, out Bounds bounds)'))
        self.assertLess(build.index('TryGetVisualBoundsLocal(root.transform, out bounds)'),
                        build.index('motor.FitShellAndWheelbase('))
        self.assertIn('TryGetAuthoredLampBodyBounds(root.transform, mesh.transform,', build)
        self.assertIn('motor.ConfigureExteriorLights(lampBounds,', build)

if __name__ == '__main__':
    unittest.main()
