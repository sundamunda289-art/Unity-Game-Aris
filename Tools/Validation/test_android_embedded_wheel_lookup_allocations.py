#!/usr/bin/env python3
"""Allocation regression for authored car wheels; no Unity/Android performance claim."""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          'Assets/AarisWorld/Scripts/AarisVehicleMotor.cs').read_text()


class EmbeddedWheelLookupSourceTests(unittest.TestCase):
    def test_cached_exact_axle_data_and_no_per_car_labels(self):
        body = SOURCE.split('public bool AttachEmbeddedWheelModels(', 1)[1].split(
            'private static bool AreAuthoredWheelAxlesPlausible(', 1)[0]
        for token in ('private static readonly string[] EmbeddedWheelTokens',
                      'private readonly List<Transform> embeddedWheelTransforms',
                      'private readonly List<Collider> embeddedWheelColliders',
                      'private readonly Transform[] embeddedWheelFound',
                      'private readonly Bounds[] embeddedWheelBounds'):
            self.assertIn(token, SOURCE)
        for token in ('string[] tokens = EmbeddedWheelTokens;',
                      'Transform[] found = embeddedWheelFound;',
                      'Bounds[] bounds = embeddedWheelBounds;',
                      'bodyRoot.GetComponentsInChildren<Transform>(true, embeddedWheelTransforms);',
                      'label.IndexOf(tokens[w], StringComparison.OrdinalIgnoreCase) >= 0',
                      'wheelVisual.GetComponentsInChildren<Collider>(',
                      'true, embeddedWheelColliders);',
                      'Array.Clear(found, 0, found.Length);',
                      'finally'):
            self.assertIn(token, body)
        self.assertNotIn('new Transform[4]', body)
        self.assertNotIn('new Bounds[4]', body)
        self.assertNotIn('GetComponentsInChildren<Transform>(true)', body)
        self.assertNotIn('GetComponentsInChildren<Collider>(true)', body)
        self.assertNotIn('ToLowerInvariant()', body)
        self.assertGreaterEqual(body.count('embeddedWheelColliders.Clear();'),2)
        self.assertEqual(body.count('embeddedWheelTransforms.Clear();'),2)
        self.assertEqual(body.count('Array.Clear(found, 0, found.Length);'),2)

    def test_original_visual_and_contact_validation_still_authoritative(self):
        body = SOURCE.split('public bool AttachEmbeddedWheelModels(', 1)[1].split(
            'private static bool AreAuthoredWheelAxlesPlausible(', 1)[0]
        for token in ('HasRenderableWheelGeometry(found[i])',
                      'TryGetVisualBoundsLocal(',
                      'transform, found[i], out bounds[i]',
                      'if (!AreAuthoredWheelAxlesPlausible(bounds)) return false;',
                      'if (obstacle != null) obstacle.enabled = false;',
                      'wheels[i].radius = Mathf.Clamp(diameter * 0.5f, .27f, .46f);',
                      'wheels[i].transform.localPosition = bounds[i].center;',
                      'visualCentreWorld - wheelVisual.position',
                      'wheelVisual.SetParent(transform, true);',
                      'visualWheelRotationOffsets[i] = restOffset;',
                      'visualWheelCenterOffsets[i] = centreOffset;',
                      'wheels[0].ResetSprungMasses();',
                      'SyncVisibleWheels();'):
            self.assertIn(token, body)
        self.assertLess(body.index('AreAuthoredWheelAxlesPlausible(bounds)'),
                        body.index('obstacle.enabled = false;'))
        self.assertNotIn('QualitySettings.', body)
        self.assertNotIn('new Material(', body)
        self.assertNotIn('Physics.Raycast', body)


if __name__ == '__main__':
    unittest.main(verbosity=2)
