#!/usr/bin/env python3
"""Verify six original SpeedTree prefabs retain near shadows but avoid far caster passes.

This is a serialized-asset / mesh-budget contract, NOT an Android FPS measurement.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / "Assets/TerrainDemoScene_URP/Prefabs/Trees"
SPECIES = (
    "Conifer/Conifer",
    "Cypress/Cypress_Forest_Desktop",
    "Pines/Pine_A/Pine_A",
    "Pines/Pine_B/Pine_B",
    "Pines/Pine_C/Pine_C",
    "Pines/Pine_D/Pine_D",
)

def read_lod_renderers(source):
    docs = re.findall(r"(?ms)^--- !u!(\d+) &(\d+)\n(.*?)(?=^--- !u!|\Z)", source)
    names = {}
    for kind, fid, body in docs:
        if kind == "1":
            m = re.search(r"(?m)^  m_Name: (.+)$", body)
            if m:
                names[fid] = m.group(1)
    lods = {}
    for kind, fid, body in docs:
        if kind != "23":
            continue
        owner = re.search(r"(?m)^  m_GameObject: \{fileID: (\d+)\}$", body)
        if not owner:
            continue
        match = re.search(r"_LOD([0-3])$", names.get(owner.group(1), ""))
        if not match:
            continue
        level = int(match.group(1))
        if level in lods:
            raise AssertionError("Duplicate SpeedTree LOD renderer")
        cast = re.search(r"(?m)^  m_CastShadows: (\d+)$", body)
        receive = re.search(r"(?m)^  m_ReceiveShadows: (\d+)$", body)
        if not cast or not receive:
            raise AssertionError("Incomplete LOD renderer shadow properties")
        lods[level] = (int(cast.group(1)), int(receive.group(1)))
    return lods

class NativeSpeedTreeMobileShadowBudget(unittest.TestCase):
    def test_near_realism_far_mobile_shadow_passes(self):
        for path in SPECIES:
            with self.subTest(species=path):
                source = (BASE / (path + ".prefab")).read_text(encoding="utf-8")
                self.assertEqual(read_lod_renderers(source), {
                    0: (1, 1), 1: (1, 1), 2: (0, 1), 3: (0, 1)
                })
                self.assertEqual(source.count("LODGroup:"), 1)
                self.assertEqual(source.count("BoxCollider:"), 1)
                self.assertEqual(source.count("MeshFilter:"), 4)
                self.assertEqual(source.count("MeshRenderer:"), 4)
                self.assertEqual(source.count("Tree:"), 4)
                self.assertIn("m_LastLODIsBillboard: 1", source)

    def test_original_mesh_linkage_and_meta_preserved(self):
        for path in SPECIES:
            with self.subTest(species=path):
                source = (BASE / (path + ".prefab")).read_text(encoding="utf-8")
                self.assertEqual(source.count("m_Mesh: {fileID: "), 4)
                self.assertTrue((BASE / (path + ".prefab.meta")).exists())
                self.assertTrue(source.startswith("%YAML"))
                self.assertNotIn("version https://git-lfs.github.com/spec/v1",source)

    def test_native_detail_prefabs_receive_without_caster_passes(self):
        # Tiny undergrowth remains fully visible and receives tree/house
        # shadows, but no longer adds extra real-time shadow caster passes.
        # Keep every native mesh, material, Unity GUID and prefab in place.
        details = sorted((ROOT / "Assets/TerrainDemoScene_URP/Prefabs/Details").glob("*.prefab"))
        self.assertEqual(len(details), 20)
        for path in details:
            with self.subTest(detail=path.stem):
                source = path.read_text(encoding="utf-8")
                self.assertEqual(re.findall(r"(?m)^  m_CastShadows: (\d+)$", source), ["0"])
                self.assertEqual(re.findall(r"(?m)^  m_ReceiveShadows: (\d+)$", source), ["1"])
                self.assertEqual(source.count("m_Mesh: {fileID: "), 1)
                self.assertTrue(path.with_suffix(".prefab.meta").exists())
                self.assertTrue(source.startswith("%YAML"))

if __name__ == "__main__":
    unittest.main(verbosity=2)
