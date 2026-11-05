"""Strict regression test for the 47 approved residential native scene scales."""
import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "Tools" / "World"))
from verify_tree_house_upgrade import (
    REVIEWED_RESIDENTIAL_SCALES, SCENE_MOBILE_LOD_PATH,
    restore_reviewed_residential_scales
)


class ResidentialScaleCoreTests(unittest.TestCase):
    def test_exact_47_root_scales_and_restore(self):
        scene = (ROOT / SCENE_MOBILE_LOD_PATH).read_bytes()
        blocks = re.split(rb"(?=^--- !u!)", scene, flags=re.M)
        names = {}
        for block in blocks:
            m = re.match(rb"--- !u!1 &(\d+)\nGameObject:\n", block)
            if m:
                n = re.search(rb"^  m_Name: ([^\r\n]+)", block, flags=re.M)
                if n:
                    names[m[1]] = n[1].decode()
        counts = {key: 0 for key in REVIEWED_RESIDENTIAL_SCALES}
        for block in blocks:
            if not re.match(rb"--- !u!4 &\d+\nTransform:\n", block):
                continue
            m = re.search(rb"^  m_GameObject: \{fileID: (\d+)\}$", block, flags=re.M)
            name = names.get(m[1], "") if m else ""
            model = next(
                (k for k in REVIEWED_RESIDENTIAL_SCALES if re.search(
                    r"_" + re.escape(k) + r"_\d+$", name)), None
            )
            if model is None:
                continue
            old, new, _ = REVIEWED_RESIDENTIAL_SCALES[model]
            scale = f"  m_LocalScale: {{x: {new}, y: {new}, z: {new}}}".encode()
            self.assertEqual(block.count(scale), 1, (name, model))
            counts[model] += 1
        self.assertEqual(sum(counts.values()), 47)
        for model, (_, _, count) in REVIEWED_RESIDENTIAL_SCALES.items():
            self.assertEqual(counts[model], count)
        restored = restore_reviewed_residential_scales(scene)
        self.assertIsNotNone(restored)
        self.assertNotEqual(restored, scene)

    def test_partial_and_out_of_scope_scale_rejected(self):
        scene = (ROOT / SCENE_MOBILE_LOD_PATH).read_bytes()
        self.assertIsNotNone(restore_reviewed_residential_scales(scene))
        marker = b"  m_LocalScale: {x: 1.456, y: 1.456, z: 1.456}"
        self.assertEqual(scene.count(marker), 5)
        partial = scene.replace(marker, b"  m_LocalScale: {x: 1.12, y: 1.12, z: 1.12}", 1)
        self.assertIsNone(restore_reviewed_residential_scales(partial))
        invalid = scene.replace(marker, b"  m_LocalScale: {x: 1.5, y: 1.5, z: 1.5}", 1)
        self.assertIsNone(restore_reviewed_residential_scales(invalid))


if __name__ == "__main__":
    unittest.main()
