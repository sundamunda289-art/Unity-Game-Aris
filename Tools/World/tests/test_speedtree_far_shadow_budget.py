"""Saved SpeedTree far-LOD shadow budget: geometry and horizon stay untouched."""
from __future__ import annotations

import pathlib
import re
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[3]
TREE = ROOT / "Assets/TerrainDemoScene_URP/Prefabs/Trees"
SOURCES = (
    "Conifer/Conifer",
    "Cypress/Cypress_Forest_Desktop",
    "Pines/Pine_A/Pine_A",
    "Pines/Pine_B/Pine_B",
    "Pines/Pine_C/Pine_C",
    "Pines/Pine_D/Pine_D",
)


def documents(text):
    # Read serialized GameObject and MeshRenderer records without rewriting Unity YAML.
    records = {}
    for match in re.finditer(r"(?ms)^--- !u!(\d+) &(\d+)\n(.*?)(?=^--- !u!|\Z)", text):
        records[match.group(2)] = (match.group(1), match.group(3))
    return records


class SpeedTreeFarShadowBudgetTests(unittest.TestCase):
    def test_far_mesh_and_billboard_cast_no_realtime_shadows(self):
        for name in SOURCES:
            with self.subTest(tree=name):
                path = TREE / (name + ".prefab")
                records = documents(path.read_text(encoding="utf-8"))
                game_objects = {}
                for obj_id, (kind, body) in records.items():
                    if kind == "1":
                        match = re.search(r"(?m)^  m_Name: ([^\r\n]+)$", body)
                        if match:
                            game_objects[obj_id] = match.group(1)
                found = {}
                for _, (kind, body) in records.items():
                    if kind != "23":
                        continue
                    owner = re.search(r"m_GameObject: \{fileID: (\d+)\}", body)
                    if not owner:
                        continue
                    obj_name = game_objects.get(owner.group(1), "")
                    lod = re.search(r"_LOD([0-3])$", obj_name)
                    if lod is None:
                        continue
                    self.assertNotIn(lod.group(1), found)
                    cast = re.search(r"(?m)^  m_CastShadows: (\d+)$", body)
                    receive = re.search(r"(?m)^  m_ReceiveShadows: (\d+)$", body)
                    self.assertIsNotNone(cast)
                    self.assertIsNotNone(receive)
                    found[lod.group(1)] = (int(cast.group(1)), int(receive.group(1)))
                self.assertEqual(found, {
                    "0": (1, 1), "1": (1, 1), "2": (0, 1), "3": (0, 1)
                })
                lods = [body for kind, body in records.values() if kind == "205"]
                self.assertEqual(len(lods), 1)
                heights = [
                    float(v) for v in re.findall(
                        r"(?m)^  - screenRelativeHeight: ([0-9.]+)$", lods[0])
                ]
                self.assertEqual(len(heights), 4)
                self.assertTrue(all(a > b > 0 for a, b in zip(heights, heights[1:])))
                self.assertIn("m_LastLODIsBillboard: 1", lods[0])


if __name__ == "__main__":
    unittest.main()
