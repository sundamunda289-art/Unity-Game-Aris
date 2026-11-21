#!/usr/bin/env python3
"""Exact source guard for reusable authored-wheel name query buffers."""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          'Assets/AarisWorld/Scripts/AarisVehicleWorld.cs').read_text('utf8')


class AuthoredWheelNameBuffer(unittest.TestCase):
    def test_no_array_per_named_wheel_lookup(self):
        body = SOURCE.split('private static Transform FindNamedWheel(Transform root, string token)', 1)[1].split(
            'internal static bool HasPlausibleAuthoredSilhouette(', 1)[0]
        self.assertIn('[ThreadStatic] private static List<Transform> namedWheelQueryScratch;', SOURCE)
        self.assertIn('root.GetComponentsInChildren<Transform>(true, all);', body)
        self.assertIn('new List<Transform>(64)', body)
        self.assertIn('all[i].name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0', body)
        self.assertIn('return all[i];', body)
        self.assertIn('finally', body)
        self.assertGreaterEqual(body.count('all.Clear();'), 2)
        self.assertNotIn('GetComponentsInChildren<Transform>(true);', body)
        self.assertNotIn('new Transform[', body)
        self.assertNotIn('ToLowerInvariant()', body)

    def test_original_axle_and_grounding_contracts_remain(self):
        for token in ('FindNamedWheel(model, "wheel_fl");',
                      'FindNamedWheel(model, "wheel_fr");',
                      'FindNamedWheel(model, "wheel_rl");',
                      'FindNamedWheel(model, "wheel_rr");',
                      'FindNamedWheel(model, names[i]);',
                      'TryGetVisualBoundsLocal(\n                    chassis, tyre, out Bounds tyreBounds)',
                      'FaceEmbeddedVehicleForward(Transform chassis,',
                      'AlignEmbeddedTyresToRoad(Transform chassis,'):
            self.assertIn(token, SOURCE)
        self.assertNotIn('QualitySettings.renderScale', SOURCE)


if __name__ == '__main__':
    unittest.main(verbosity=2)
