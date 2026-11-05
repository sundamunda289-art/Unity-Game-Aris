"""Exact scene-integrity proof for 47 residential and 32 rock-cover LOD groups.

These checks validate serialized YAML only, not rendered image quality or Android FPS.
"""
import hashlib
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'Tools/World'))
from verify_tree_house_upgrade import (SCENE_MOBILE_LOD_PATH, restore_scene_mobile_lod, restore_reviewed_scene_far_shadow, restore_reviewed_residential_scales, restore_reviewed_residential_foundations, restore_reviewed_residential_vehicle_spawns)


class SavedSceneMobileLodIntegrity(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scene = (ROOT / SCENE_MOBILE_LOD_PATH).read_bytes()
        cls.contract = json.loads(
            (ROOT / 'Tools/World/tree-house-20261103.postconditions.json').read_text()
        )

    def test_exact_original_scene_restored_by_only_reviewed_lod_changes(self):
        restored_vehicle = restore_reviewed_residential_vehicle_spawns(ROOT, self.scene)
        self.assertIsNotNone(restored_vehicle)
        restored_foundations = restore_reviewed_residential_foundations(ROOT, restored_vehicle)
        self.assertIsNotNone(restored_foundations)
        restored_scales = restore_reviewed_residential_scales(restored_foundations)
        self.assertIsNotNone(restored_scales)
        restored_shadows = restore_reviewed_scene_far_shadow(restored_scales)
        self.assertIsNotNone(restored_shadows)
        original = restore_scene_mobile_lod(restored_shadows)
        self.assertIsNotNone(original, 'Unexpected scene LOD counts, identities or thresholds')
        expected = self.contract['preserved_files'][SCENE_MOBILE_LOD_PATH]
        self.assertEqual(hashlib.sha256(original).hexdigest(), expected)
        self.assertEqual(self.scene.count(b'  - screenRelativeHeight: 0.28\n'), 79)
        self.assertEqual(self.scene.count(b'  - screenRelativeHeight: 0.095\n'), 79)
        self.assertEqual(self.scene.count(b'  - screenRelativeHeight: 0.22\n'), 22)
        self.assertEqual(self.scene.count(b'  - screenRelativeHeight: 0.07\n'), 19)
        self.assertEqual(self.scene.count(b'  - screenRelativeHeight: 0.11\n'), 3)
        self.assertEqual(self.scene.count(b'  - screenRelativeHeight: 0.0025\n'), 101)

    def test_three_heavy_compound_prefabs_restore_to_exact_protected_sources(self):
        from verify_tree_house_upgrade import (
            HEAVY_COMPOUND_SOURCE_PREFABS, preserved_or_reviewed_lod_only, restore_reviewed_far_shadow
        )
        self.assertEqual(len(HEAVY_COMPOUND_SOURCE_PREFABS), 3)
        for path in sorted(HEAVY_COMPOUND_SOURCE_PREFABS):
            with self.subTest(path=path):
                source = (ROOT / path).read_bytes()
                self.assertEqual(source.count(b'  - screenRelativeHeight: 0.11\n'), 1)
                self.assertEqual(source.count(b'  - screenRelativeHeight: 0.22\n'), 1)
                self.assertEqual(source.count(b'  - screenRelativeHeight: 0.0025\n'), 1)
                before_shadow=restore_reviewed_far_shadow(source,path)
                self.assertIsNotNone(before_shadow)
                old = before_shadow.replace(b'  - screenRelativeHeight: 0.11\n',
                                     b'  - screenRelativeHeight: 0.07\n', 1)
                self.assertEqual(hashlib.sha256(old).hexdigest(),
                                 self.contract['preserved_files'][path])
                self.assertTrue(preserved_or_reviewed_lod_only(
                    ROOT, path, self.contract['preserved_files'][path]))
        changed = self.scene.replace(b'  - screenRelativeHeight: 0.11\n',
                                     b'  - screenRelativeHeight: 0.12\n', 1)
        self.assertIsNone(restore_scene_mobile_lod(changed))

    def test_modified_scene_geometry_or_metadata_cannot_pass(self):
        modified = self.scene.replace(b'  m_Fog: 1\n', b'  m_Fog: 0\n', 1)
        self.assertNotEqual(modified, self.scene)
        restored_vehicle = restore_reviewed_residential_vehicle_spawns(ROOT, modified)
        self.assertIsNotNone(restored_vehicle)
        restored_foundations = restore_reviewed_residential_foundations(ROOT, restored_vehicle)
        self.assertIsNotNone(restored_foundations)
        restored_scales = restore_reviewed_residential_scales(restored_foundations)
        self.assertIsNotNone(restored_scales)
        restored_shadows = restore_reviewed_scene_far_shadow(restored_scales)
        self.assertIsNotNone(restored_shadows)
        restored = restore_scene_mobile_lod(restored_shadows)
        self.assertIsNotNone(restored)
        expected = self.contract['preserved_files'][SCENE_MOBILE_LOD_PATH]
        self.assertNotEqual(hashlib.sha256(restored).hexdigest(), expected)

    def test_unexpected_threshold_fails_closed(self):
        changed = self.scene.replace(
            b'  - screenRelativeHeight: 0.28\n',
            b'  - screenRelativeHeight: 0.29\n', 1
        )
        self.assertNotEqual(changed, self.scene)
        self.assertIsNone(restore_scene_mobile_lod(changed))


if __name__ == '__main__':
    unittest.main()
