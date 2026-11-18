#!/usr/bin/env python3
"""Protect opaque glazed treatment of legacy non-URP imported car windows.

Static vehicle-material contract only; not an Android FPS or visual benchmark.
"""
from pathlib import Path
import unittest

WORLD = (Path(__file__).resolve().parents[2] /
         'Assets/AarisWorld/Scripts/AarisVehicleWorld.cs').read_text('utf-8')

class LegacyAutomotiveGlass(unittest.TestCase):
    def test_legacy_glass_has_no_transparency_overdraw(self):
        snippet = WORLD.split('            if (glass)\n', 1)[1].split(
            '            else if (headlight || taillight)', 1)[0]
        for expected in ('smoked.a = 1f;', '"RenderType", "Opaque"',
                         '"_Surface", 0f', 'UnityEngine.Rendering.BlendMode.One',
                         'UnityEngine.Rendering.BlendMode.Zero',
                         '"_ZWrite", 1f',
                         'result.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");',
                         'UnityEngine.Rendering.RenderQueue.Geometry',
                         'result.SetFloat("_Smoothness", .81f)'):
            self.assertIn(expected, snippet)
        for forbidden in ('"RenderType", "Transparent"',
                          'result.EnableKeyword("_SURFACE_TYPE_TRANSPARENT")',
                          'RenderQueue.Transparent', 'new Material(', 'renderer.material'):
            self.assertNotIn(forbidden, snippet)

    def test_native_urp_imports_still_keep_source_surface(self):
        section = WORLD.split('private Material AuthoredUrpMaterial(', 1)[1].split(
            'Shader shader = Shader.Find(', 1)[0]
        self.assertIn('source.shader.name == "Universal Render Pipeline/Lit"', section)
        self.assertIn('bool separateGlass', section)
        self.assertIn('return source;', section)
        self.assertIn('new Material(source)', section)
        self.assertIn('authoredMaterialCache[source] = polished;', section)

if __name__ == '__main__':
    unittest.main()
