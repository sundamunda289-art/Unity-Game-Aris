"""Native Unity district partition contracts. Source + serialized scene preflight.
Never treats source-only assertions as an Editor build or phone FPS measurement.
"""
import unittest
from pathlib import Path
import sys
import re
from collections import Counter

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Tools/Validation"))
from world_partition_readiness import WorldScene

MAIN = ROOT / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"
PARTITIONER = ROOT / "Assets/AarisWorld/Editor/AarisDistrictWorldPartitionBatch.cs"
META = ROOT / "Assets/AarisWorld/Editor/AarisDistrictWorldPartitionBatch.cs.meta"
BUILD = ROOT / "ProjectSettings/EditorBuildSettings.asset"
CHUNKS = ROOT / "Assets/AarisWorld/Scenes/Chunks"
ULTRA = ROOT / "Assets/AarisWorld/Editor/AarisUltraSectorBake.cs"
# Original saved MobileRuntime/AarisNearbyColliders script identity. Keep the
# authoritative collider owner separate from new presentation-only references.
NEARBY_COLLIDERS_GUID = "f99aebde001f56119c583c593f64c271"

DISTRICTS = (
    "CanalMarket", "ClockTowerTown", "CoastalHamlet", "EasternPort",
    "FreightDistrict", "HighlandObservatory", "LakesideHamlet",
    "NorthernRidgeTown", "OldVillage", "SoutheastLighthouse",
    "SouthernAirfield", "ValleySettlement",
)


def validated_cohort_presentation_links(scene, root):
    """Allow only proven cohort source-renderer links, never collider owners.

    The shared structural gate verifies exact source/mesh/material ownership,
    generated outputs, saved roots, active fallback scenes and native assets
    before any incoming reference is classified as presentation-only.
    """
    from test_world_partition_readiness import baked_static_inventory
    generated, _ = baked_static_inventory(root)
    if not generated:
        return set()
    metadata = root / 'Assets/AarisWorld/Scripts/AarisBakedStaticBatch.cs.meta'
    match = re.search(r'(?m)^guid: ([0-9a-f]{32})$', metadata.read_text())
    assert match, 'Missing presentation controller script identity'
    script = '{fileID: 11500000, guid: ' + match[1] + ', type: 3}'
    links = set()
    for uid, block in scene.blocks.items():
        if block.kind != 114 or not re.search(
                r'(?m)^  m_Script: ' + re.escape(script) + '$', block.body):
            continue
        for target in re.findall(r'(?m)^  - renderer: \{fileID: (-?\d+)\}$', block.body):
            target = int(target)
            assert target in scene.blocks and scene.blocks[target].kind == 23, \
                'Presentation controller must not acquire a collider reference'
            links.add((uid, target))
    assert links, 'Generated cohorts have no validated source-renderer references'
    return links


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
        presentation_links = validated_cohort_presentation_links(scene, ROOT)
        grid_paths = re.findall(r"(?m)^\s+path: (Assets/AarisWorld/Scenes/Chunks/Aaris_Cell_[^\r\n]+\.unity)$", BUILD.read_text())
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
                    gameplay_links = incoming - presentation_links
                    self.assertEqual(len(gameplay_links), 1, 'Persistent collider must retain exactly one gameplay owner')
                    owner, collider = next(iter(gameplay_links))
                    self.assertEqual(scene.blocks[collider].kind, 64)
                    self.assertEqual(scene.owner[collider], go)
                    self.assertEqual(scene.blocks[owner].kind, 114)
                    self.assertIn('m_Script: {fileID: 11500000, guid: ' + NEARBY_COLLIDERS_GUID + ', type: 3}',
                                  scene.blocks[owner].body)
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
                guid = re.search(r"(?m)^guid: ([0-9a-f]{32})$", meta.read_text())
                self.assertIsNotNone(guid)
                if grid_paths:
                    # The original district scenes remain byte-preserved
                    # source archives; only the repackaged cells ship active.
                    self.assertNotIn(guid.group(1), BUILD.read_text())
                    self.assertNotIn(path.relative_to(ROOT).as_posix(), BUILD.read_text())
                else:
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
        if grid_paths:
            registered = re.findall(r"(?m)^  - scenePath: ([^\r\n]+)$", MAIN.read_text())
            fallback_paths = re.findall(r"(?m)^  chunkScenePath: ([^\r\n]+)$", MAIN.read_text())
            self.assertEqual(set(grid_paths), set(registered))
            self.assertEqual(len(grid_paths), len(set(grid_paths)))
            self.assertTrue(set(fallback_paths).issubset(set(grid_paths)))
            for path in grid_paths:
                self.assertTrue((ROOT / path).is_file())
                self.assertTrue((ROOT / (path + ".meta")).is_file())

    def test_sector_repack_requires_unchanged_collision_and_source_archives(self):
        s = ULTRA.read_text()
        self.assertLess(s.index('"unsaved-scene-work"'),
                        s.index("EditorSceneManager.OpenScene(Main, OpenSceneMode.Single)"))
        self.assertIn("SceneManager.MoveGameObjectToScene(item.root, scene);", s)
        self.assertIn("item.geometryFingerprint == GeometryFingerprint(item.root)", s)
        self.assertIn("FileHash(original.Key) == original.Value", s)
        self.assertIn("persistentBefore == PersistentFingerprint(bootstrap)", s)
        self.assertIn("changed-paths.txt", s)
        self.assertIn("public static void VerifySaved()", s)
        self.assertIn("EditorSceneManager.OpenScene(cell.scenePath, OpenSceneMode.Additive)", s)
        self.assertIn("GeometryFingerprint(found) == record.geometryFingerprint", s)
        self.assertIn("LodFingerprint(found) == record.lodFingerprint", s)
        self.assertIn("packaged.SetEquals(registered)", s)
        self.assertIn("FileHash(archive.path) == archive.sha256", s)
        self.assertIn('"verification-wrote-project-file"', s)
        self.assertIn("GlobalObjectId.GetGlobalObjectIdSlow(value)", s)
        self.assertNotIn("AssetDatabase.SaveAssets", s)
        self.assertNotIn("TerrainData.SetHeights", s)
        self.assertNotIn("DestroyImmediate", s)


if __name__ == "__main__":
    unittest.main(verbosity=2)
