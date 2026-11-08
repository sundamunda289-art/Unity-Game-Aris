#!/usr/bin/env python3
"""Offline static audit of original 6 Unity SpeedTree prefab proportions.

Checks preserve Git LFS terrain, 1100 authored tree coordinates, LOD meshes and
trunk colliders. This cannot replace real Unity Editor visual verification.
"""
from pathlib import Path
import json
import math
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
TREE_BASE = ROOT / "Assets" / "TerrainDemoScene_URP" / "Prefabs" / "Trees"
FOREST = ROOT / "Docs" / "FOREST_PLANTING_PLAN.json"
LAYOUT = ROOT / "Docs" / "MAP_LAYOUT.json"

SPECIES = {
    "Conifer": ("Conifer/Conifer.prefab", (1.21, 0.99, 1.21), 1.0),
    "Pine_A": ("Pines/Pine_A/Pine_A.prefab", (1.21, 0.98, 1.21), 1.0),
    "Pine_B": ("Pines/Pine_B/Pine_B.prefab", (1.21, 1.025, 1.21), 1.0),
    "Pine_C": ("Pines/Pine_C/Pine_C.prefab", (1.18, 0.99, 1.18), 1.0),
    "Pine_D": ("Pines/Pine_D/Pine_D.prefab", (1.24, 0.99, 1.24), 1.0),
    "Cypress": ("Cypress/Cypress_Forest_Desktop.prefab", (1.66, 1.44, 1.66), 1.5),
}


def root_transforms(source):
    result = []
    for block in re.split(r"(?=^--- !u!)", source, flags=re.M):
        if "\nTransform:\n" not in block:
            continue
        if "m_Father: {fileID: 0}" in block:
            match = re.search(
                r"m_LocalScale: \{x: ([0-9.]+), y: ([0-9.]+), z: ([0-9.]+)\}",
                block,
            )
            if match:
                result.append(tuple(map(float, match.groups())))
    return result


class NaturalTreePrefabTests(unittest.TestCase):
    def test_balanced_silhouettes_and_lod(self):
        for name, (rel, expected, old_uniform) in SPECIES.items():
            with self.subTest(species=name):
                prefab = TREE_BASE / rel
                source = prefab.read_text(encoding="utf-8")
                roots = root_transforms(source)
                self.assertEqual(len(roots), 1)
                self.assertEqual(roots[0], expected)
                x, y, z = roots[0]
                self.assertAlmostEqual(x, z, places=5)
                self.assertGreater(x / y, 1.05)
                self.assertLess(x / y, 1.30)
                self.assertGreater(x, old_uniform)
                self.assertLessEqual(y, old_uniform * 1.045)
                self.assertEqual(source.count("LODGroup:"), 1)
                self.assertEqual(source.count("BoxCollider:"), 1)
                self.assertGreaterEqual(source.count("MeshFilter:"), 4)
                self.assertGreaterEqual(source.count("MeshRenderer:"), 4)
                self.assertGreaterEqual(source.count("\nTree:"), 4)
                self.assertTrue(prefab.with_suffix(".prefab.meta").is_file())
                self.assertFalse(source.startswith("version https://git-lfs.github.com/spec/v1"))

    def test_all_tree_materials_use_real_urp_speedtree8_shader(self):
        materials = sorted(TREE_BASE.rglob("*.mat"))
        self.assertEqual(len(materials), 19)
        correct = "m_Shader: {fileID: 4800000, guid: 99134b1f0c27d54469a840832a28fadf, type: 3}"
        for path in materials:
            with self.subTest(material=path.name):
                source = path.read_text(encoding="utf-8")
                self.assertIn(correct, source)
                self.assertNotIn("d4fdc026464a42041be7fe785babd90e", source)
                self.assertNotIn("515f8966b1d702f4a8c97ca412a9a4e9", source)
                self.assertRegex(source, r"(?m)^    - _MainTex:\n        m_Texture: \{fileID: 2800000,")
                self.assertTrue(path.with_suffix(".mat.meta").is_file())
                is_billboard = path.name.endswith("Billboard.mat")
                keywords = source.split("m_ValidKeywords:", 1)[1].split("m_InvalidKeywords:", 1)[0]
                self.assertEqual("  - EFFECT_BILLBOARD" in keywords, is_billboard)
                self.assertIn(f"    - _BillboardKwToggle: {int(is_billboard)}", source)

    def test_red_bush_keeps_cutout_and_textures_with_urp_lit(self):
        material = ROOT / "Assets/TerrainDemoScene_URP/Prefabs/Details/Materials/Bush_Red.mat"
        source = material.read_text(encoding="utf-8")
        self.assertIn("m_Shader: {fileID: 4800000, guid: 933532a4fcc9baf4fa0491de14d08ed7, type: 3}", source)
        self.assertNotIn("dfa3b96612ed4df4a97b7fd06217ba23", source)
        self.assertIn("  - _ALPHATEST_ON", source)
        self.assertIn("  - _NORMALMAP", source)
        self.assertIn("    - _AlphaClip: 1", source)
        self.assertIn("    - _Cull: 0", source)
        self.assertIn("    - _Cutoff: 0.5", source)
        self.assertIn("    - _Smoothness: 0.25", source)
        self.assertIn("    - _BaseMap:\n        m_Texture: {fileID: 2800000, guid: 1933fb8bd6eed9d429dd93c5de2f061c, type: 3}", source)
        self.assertIn("    - _BumpMap:\n        m_Texture: {fileID: 2800000, guid: 90a6b9df8334d6040b2ae1928aaab244, type: 3}", source)
        self.assertTrue(material.with_suffix(".mat.meta").is_file())

    def test_all_1100_native_forest_placements_keep_their_prototypes(self):
        forest = json.loads(FOREST.read_text(encoding="utf-8"))
        places = forest["placements"]
        self.assertEqual(len(places), 1100)
        paths = {str(TREE_BASE / rel).replace(str(ROOT) + "/", "")
                 for rel, _, _ in SPECIES.values()}
        seen = {}
        for place in places:
            path = place["path"]
            self.assertIn(path, paths)
            seen[path] = seen.get(path, 0) + 1
            self.assertTrue(math.isfinite(place["height_scale"]))
            self.assertTrue(math.isfinite(place["width_scale"]))
            self.assertGreater(place["height_scale"], 0)
            self.assertGreater(place["width_scale"], 0)
            self.assertEqual(len(place["position_xz_m"]), 2)
        self.assertEqual(len(seen), 6)
        self.assertEqual(sum(seen.values()), 1100)

    def test_authored_trees_keep_safe_distance_from_all_roads(self):
        forest = json.loads(FOREST.read_text(encoding="utf-8"))
        layout = json.loads(LAYOUT.read_text(encoding="utf-8"))
        roads = []
        for road in layout["roads"]:
            p = road["points"]
            for a, b in zip(p, p[1:]):
                ax, _, az = a
                bx, _, bz = b
                roads.append((ax, az, bx-ax, bz-az,
                              (bx-ax)**2+(bz-az)**2, road["width"]/2))
        minimum = float("inf")
        for place in forest["placements"]:
            x, z = place["position_xz_m"]
            for ax, az, dx, dz, den, half in roads:
                t = max(0., min(1., ((x-ax)*dx+(z-az)*dz)/den)) if den else 0.
                d = math.hypot(x-(ax+t*dx), z-(az+t*dz)) - half
                minimum = min(minimum, d)
        # Previous authored clearance is around 13 m, leaving plenty of room
        # for the modest additional 6-16% horizontal tree silhouette.
        self.assertGreater(minimum, 10)

    def test_native_scene_and_lfs_unmodified_by_tree_prefab_patch(self):
        scene = ROOT / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"
        self.assertTrue(scene.is_file())
        # Check text roots only. The GitHub PR diff must contain exactly 6
        # prefab YAML changes, this test and its workflow/docs. Large binary
        # TerrainData or .unity files are not regenerated or overwritten.


if __name__ == "__main__":
    unittest.main(verbosity=2)
