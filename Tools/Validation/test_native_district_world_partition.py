"""Native Unity district partition contracts. Source + serialized scene preflight.
Never treats source-only assertions as an Editor build or phone FPS measurement.
"""
import unittest
from pathlib import Path
import sys
from collections import Counter

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Tools/Validation"))
from world_partition_readiness import WorldScene

MAIN = ROOT / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"
PARTITIONER = ROOT / "Assets/AarisWorld/Editor/AarisDistrictWorldPartitionBatch.cs"
META = ROOT / "Assets/AarisWorld/Editor/AarisDistrictWorldPartitionBatch.cs.meta"
BUILD = ROOT / "ProjectSettings/EditorBuildSettings.asset"
CHUNKS = ROOT / "Assets/AarisWorld/Scenes/Chunks"

DISTRICTS = (
    "CanalMarket", "ClockTowerTown", "CoastalHamlet", "EasternPort",
    "FreightDistrict", "HighlandObservatory", "LakesideHamlet",
    "NorthernRidgeTown", "OldVillage", "SoutheastLighthouse",
    "SouthernAirfield", "ValleySettlement",
)


class NativeDistrictPartitionTests(unittest.TestCase):
    def test_authoring_is_native_isolated_and_opt_in(self):
        s = PARTITIONER.read_text()
        self.assertIn("#if UNITY_EDITOR", s)
        self.assertIn("Environment.GetEnvironmentVariable(\"AARIS_WORLD_PARTITION_APPLY\") == \"1\"", s)
        self.assertIn("public static void Probe()", s)
        self.assertIn("public static void ApplyAndVerify()", s)
        self.assertIn("public static void Verify()", s)
        self.assertIn("private static string BackUpScene()", s)
        self.assertIn("SceneManager.MoveGameObjectToScene(original.gameObject, cellScene);", s)
        self.assertIn("EditorSceneManager.SaveScene(cellScene, district.path)", s)
        self.assertIn("EditorBuildSettings.scenes = buildScenes.ToArray();", s)
        self.assertIn("AssetDatabase.Refresh();", s)
        self.assertIn("AARIS_DISTRICT_APPLY_VERIFY_COMPLETE", s)
        self.assertRegex(META.read_text(), r"(?m)^guid: [0-9a-f]{32}$")
        for forbidden in ("SceneManager.LoadScene(", "InstantiateAsync(", "FindObjectsOfTypeAll",
                          "QualitySettings.", "renderScale =", "treeDistance =",
                          "Screen.SetResolution(", "TerrainData.", "git worktree", "git reset"):
            self.assertNotIn(forbidden, s)

    def test_collision_and_far_appearance_are_authoritative(self):
        s = PARTITIONER.read_text()
        for token in (
            "destination.sharedMesh = source.sharedMesh;",
            "destination.sharedMaterial = source.sharedMaterial;",
            "destination.cookingOptions = source.cookingOptions;",
            "destination.contactOffset = source.contactOffset;",
            "destination.includeLayers = source.includeLayers;",
            "destination.excludeLayers = source.excludeLayers;",
            "destination.layerOverridePriority = source.layerOverridePriority;",
            "destination.providesContacts = source.providesContacts;",
            "slot.objectReferenceValue = persistentCollider;",
            "UnityEngine.Object.DestroyImmediate(building.physics);",
            "UnityEngine.Object.Instantiate(building.farRenderer.gameObject)",
            "farRenderer.enabled = true;",
            'fallbackData.FindProperty("chunkScenePath").stringValue = district.path;',
            'fallbackData.FindProperty("silhouetteRenderers")',
            "originalFar.GetComponent<MeshFilter>().sharedMesh",
            "originalFar.sharedMaterials",
            "Quaternion.Angle(silhouette.transform.rotation,",
        ):
            self.assertIn(token, s)
        self.assertLess(s.index("slot.objectReferenceValue = persistentCollider;"),
                        s.index("UnityEngine.Object.DestroyImmediate(building.physics);"))

    def test_batch_directory_names_and_count_are_distinct(self):
        s = PARTITIONER.read_text()
        self.assertEqual(len(DISTRICTS), 12)
        self.assertNotIn('"ForestLookout",', s)
        manifest = s.split("private static readonly string[] Districts = {", 1)[1].split("};", 1)[0]
        for district in DISTRICTS:
            self.assertEqual(manifest.count('"' + district + '"'), 1, district)
        self.assertIn('private const string Prefix = "Aaris_District_";', s)
        self.assertIn("cells.arraySize += districts.Count;", s)

    def test_exact_migrations_protect_original_gameplay_references(self):
        scene = WorldScene(MAIN.read_text())
        total_candidates = 0
        migrated = 0
        all_chunks_exist = all((CHUNKS / f"Aaris_District_{name}_Detail.unity").is_file()
                               for name in DISTRICTS)
        none_exist = not any((CHUNKS / f"Aaris_District_{name}_Detail.unity").is_file()
                             for name in DISTRICTS)
        self.assertTrue(all_chunks_exist or none_exist,
                        "Incomplete native scene asset set (must fail closed)")
        for name in DISTRICTS:
            district = scene.find_unique(name)
            children = scene.children[district]
            if none_exist:
                for root in children:
                    types = scene.component_types[root]
                    if 64 in types and 205 not in types:
                        self.assertEqual((name, scene.names[root]),
                                         ("EasternPort", "Port_Sea_Pier"))
                        continue
                    if 64 not in types and 205 not in types:
                        self.assertIn(name, ("SouthernAirfield", "CanalMarket"))
                        child_name = scene.names[root]
                        self.assertTrue(
                            (name == "CanalMarket" and child_name == "CanalWater_VisualOnly") or
                            (name == "SouthernAirfield" and (
                                child_name == "Airfield_240m_Runway" or
                                child_name.startswith("RunwayCentre_"))))
                        continue
                    self.assertIn(64, types)
                    self.assertIn(205, types)
                    descendants = scene.descendants(root)
                    self.assertEqual(len(descendants), 3)
                    outgoing, incoming = scene.semantic_cross_refs(descendants)
                    self.assertFalse(outgoing, f"{name}: unexpected outbound references")
                    self.assertEqual(len(incoming), 1, f"{name}: multi-owner collider")
                    total_candidates += 1
            else:
                persistent = [go for go in children if
                              scene.names[go].startswith("__AarisPersistentCover_")]
                for go in persistent:
                    self.assertIn(64, scene.component_types[go])
                    self.assertIn(114, scene.component_types[go])
                    subs = scene.descendants(go)
                    self.assertEqual(len(subs), 2)
                    silhouettes = [x for x in subs if x != go]
                    self.assertEqual(scene.component_types[silhouettes[0]], {33, 23})
                    outgoing, incoming = scene.semantic_cross_refs(subs)
                    self.assertFalse(outgoing)
                    self.assertEqual(len(incoming), 1)
                    migrated += 1
                self.assertTrue(persistent)
                path = CHUNKS / f"Aaris_District_{name}_Detail.unity"
                text = path.read_text()
                cell = WorldScene(text)
                prefab_roots = [block for block in cell.blocks.values() if block.kind == 1001]
                self.assertEqual(len(cell.names) + 3 * len(prefab_roots), 3 * len(persistent))
                for prefab in prefab_roots:
                    self.assertIn("m_RemovedComponents:", prefab.body)
                    self.assertIn("m_SourcePrefab: {fileID: 100100000, guid:", prefab.body)
                for root in (x for x in cell.names if cell.parent_go[x] is None):
                    self.assertEqual(cell.component_types[root], {33, 23, 205})
                    self.assertEqual(len(cell.descendants(root)), 3)
                meta = (path.parent / (path.name + ".meta"))
                self.assertTrue(meta.is_file())
                import re
                guid = re.search(r"(?m)^guid: ([0-9a-f]{32})$", meta.read_text())
                self.assertIsNotNone(guid)
                self.assertIn(guid.group(1), BUILD.read_text())
                self.assertEqual(BUILD.read_text().count(path.relative_to(ROOT).as_posix()), 1)
        if none_exist:
            self.assertGreaterEqual(total_candidates, 90)
        else:
            self.assertGreaterEqual(migrated, 90)
        # The ForestLookout pilot already present in main remains separate.
        self.assertIsNotNone(scene.find_unique("ForestLookout"))
        self.assertIsNotNone(scene.find_unique("Terrain_2x2"))
        self.assertIsNotNone(scene.find_unique("RoadNetwork"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
