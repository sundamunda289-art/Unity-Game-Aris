#!/usr/bin/env python3
"""Source regressions for real mesh bounds with reusable Unity 6 buffers."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / 'Assets/AarisWorld/Scripts/AarisVehicleWorld.cs').read_text('utf8')


class VehicleBoundsReuse(unittest.TestCase):
    def test_reuse_caller_owned_component_lists(self):
        section = SOURCE.split(
            'internal static bool TryGetVisualBoundsLocal(Transform localSpace,\n'
            '            Transform subtree, out Bounds combined)', 1)[1].split(
                'private static void AccumulateLocalBounds(', 1)[0]
        for token in (
            '[ThreadStatic] private static List<MeshFilter> visualBoundsFilters;',
            '[ThreadStatic] private static List<SkinnedMeshRenderer> visualBoundsSkins;',
            'subtree.GetComponentsInChildren<MeshFilter>(true, filters);',
            'subtree.GetComponentsInChildren<SkinnedMeshRenderer>(true, skins);',
            'filters.Clear();', 'skins.Clear();',
            'finally', 'List<MeshFilter> filters = visualBoundsFilters;',
            'List<SkinnedMeshRenderer> skins = visualBoundsSkins;'
        ):
            self.assertIn(token, SOURCE)
        self.assertNotIn('GetComponentsInChildren<MeshFilter>(true)', section)
        self.assertNotIn('GetComponentsInChildren<SkinnedMeshRenderer>(true)', section)
        self.assertEqual(section.count('AccumulateLocalBounds('), 2)
        self.assertEqual(section.count('filters.Clear();'), 2)
        self.assertEqual(section.count('skins.Clear();'), 2)

    def test_original_fitted_geometry_constraints_retained(self):
        section = SOURCE.split(
            'internal static bool TryGetVisualBoundsLocal(Transform localSpace,\n'
            '            Transform subtree, out Bounds combined)', 1)[1].split(
                'private static void AccumulateLocalBounds(', 1)[0]
        for token in (
            'Matrix4x4 toLocal = localSpace.worldToLocalMatrix;',
            'filter.sharedMesh.vertexCount == 0',
            'renderer == null || !renderer.enabled',
            'renderer.forceRenderingOff',
            '!renderer.gameObject.activeInHierarchy',
            'skinned.sharedMesh.vertexCount == 0 || !skinned.enabled',
            'skinned.forceRenderingOff',
            '!skinned.gameObject.activeInHierarchy',
            'toLocal * filter.transform.localToWorldMatrix',
            'toLocal * skinned.transform.localToWorldMatrix',
            'ref combined, ref found',
        ):
            self.assertIn(token, section)
        self.assertNotIn('renderer.bounds', section)
        self.assertNotIn('QualitySettings.', section)
        self.assertNotIn('new Material(', section)


if __name__ == '__main__':
    unittest.main(verbosity=2)
