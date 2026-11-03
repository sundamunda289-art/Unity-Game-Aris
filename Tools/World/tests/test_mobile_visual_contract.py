"""Regression guards for saved source proportions and RAM-aware mobile settings.

These checks need no Unity Editor or LFS geometry. They cannot prove that a tree
looks realistic, that a door works, or that a device reaches a frame-rate target.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[3]
TREE_PREFABS = {
    "Cypress_Forest_Desktop": "Cypress/Cypress_Forest_Desktop.prefab",
    "Conifer": "Conifer/Conifer.prefab",
    "Pine_A": "Pines/Pine_A/Pine_A.prefab",
    "Pine_B": "Pines/Pine_B/Pine_B.prefab",
    "Pine_C": "Pines/Pine_C/Pine_C.prefab",
    "Pine_D": "Pines/Pine_D/Pine_D.prefab",
}
TREES = ROOT / "Assets/TerrainDemoScene_URP/Prefabs/Trees"


def tree_root_scale(path, name):
    source = path.read_text(encoding="utf-8")
    anchor = re.search(r"^  m_Name: " + re.escape(name) + r"$", source, re.MULTILINE)
    if anchor is None:
        raise AssertionError(f"Missing prefab root: {name}")
    # All supplied source prefabs serialize their root GameObject then its Transform.
    after = source[anchor.end():]
    transform = re.search(r"^--- !u!4 &\d+\nTransform:", after, re.MULTILINE)
    if transform is None or transform.start() > 500:
        raise AssertionError(f"Missing adjacent root transform: {name}")
    scale = re.search(
        r"^  m_LocalScale: \{x: ([-.\d]+), y: ([-.\d]+), z: ([-.\d]+)\}$",
        after[transform.end():], re.MULTILINE,
    )
    if scale is None or scale.start() > 700:
        raise AssertionError(f"Missing root scale: {name}")
    return tuple(map(float, scale.groups())), source


class SavedProportions(unittest.TestCase):
    def test_wider_than_tall_tree_profiles(self):
        self.assertEqual(len(TREE_PREFABS), 6)
        for name, relative_path in TREE_PREFABS.items():
            path = TREES / relative_path
            with self.subTest(tree=name):
                (x, y, z), source = tree_root_scale(path, name)
                self.assertAlmostEqual(x, z, places=6)
                self.assertGreater(y, 0)
                self.assertGreaterEqual(x / y, 1.08)
                self.assertLessEqual(x / y, 1.32)
                self.assertIn("LODGroup:", source)
                self.assertIn("BoxCollider:", source)
                meta = path.with_suffix(path.suffix + ".meta").read_text()
                self.assertRegex(meta, r"(?m)^guid: [0-9a-f]{32}$")

    def test_mobile_budget_keeps_full_resolution_and_distant_geometry(self):
        source = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text()
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", source)
        self.assertIn("ApplyMobileMemoryProfile();", source)
        self.assertIn("systemRamMb <= 2304", source)
        self.assertIn("256f : 320f", source)
        self.assertIn("mobilePipeline.shadowDistance =", source)
        self.assertIn("mainLightShadowmapResolution =", source)
        self.assertNotIn("renderScale =", source)
        self.assertNotIn("farClipPlane =", source)
        self.assertNotIn("GC.Collect(", source)

    def test_collider_registry_does_not_force_disabled_collision_on(self):
        source = (ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text()
        self.assertIn("savedEnabled[i] = c.enabled;", source)
        self.assertIn("SphereCollider", source)
        self.assertIn("CapsuleCollider", source)
        self.assertIn("if (changedColliders) Physics.SyncTransforms();", source)
        self.assertIn("obstacles[i].enabled = savedEnabled[i];", source)


if __name__ == "__main__":
    unittest.main()
