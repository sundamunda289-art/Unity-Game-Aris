"""Source contracts for opt-in native ForestLookout world-cell migration.

These do not replace a real Unity Editor compile, scene reopen or Android FPS QA.
"""
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Tools/Validation"))
from world_partition_readiness import WorldScene
EDITOR = (ROOT / "Assets/AarisWorld/Editor/AarisForestLookoutPartitionPilot.cs")
RUNTIME = (ROOT / "Assets/AarisWorld/Scripts/AarisChunkFallbackVisibility.cs")


class ForestLookoutPilotSourceTests(unittest.TestCase):
    def test_editor_migration_is_privately_gated_and_targeted(self):
        s = EDITOR.read_text()
        self.assertIn('AARIS_WORLD_PARTITION_APPLY', s)
        self.assertIn('public static void Probe()', s)
        self.assertIn('public static void Apply()', s)
        self.assertIn('public static void VerifyAuthoredPilot()', s)
        self.assertIn('public static void ApplyAndVerify()', s)
        self.assertIn('AARIS_FORESTLOOKOUT_PILOT_APPLY_VERIFY_COMPLETE', s)
        self.assertIn('Apply();\n            VerifyAuthoredPilot();', s)
        self.assertIn('AARIS_FORESTLOOKOUT_PILOT_VERIFY_PASS', s)
        self.assertIn('Check(matches == 1, "physical-proxy-not-rebound");', s)
        self.assertIn('persistent-collider-source-mesh-identity-changed', s)
        self.assertIn('"6d0fe0e15b4c57e9ba9d921e437cc280"', s)
        self.assertIn('OriginalColliderLocalId = 4300000L;', s)
        self.assertIn('sourceGuid == OriginalColliderGuid', s)
        self.assertIn('sourceLocalId == OriginalColliderLocalId', s)
        self.assertIn('authored-collision-mesh-changed-before-migration', s)
        self.assertIn('private static string BackupBeforeAssetMutation()', s)
        self.assertIn('Path.GetTempPath()', s)
        self.assertIn('File.Copy(MainPath, originalCopy);', s)
        self.assertIn('File.Copy(buildSettings, settingsCopy);', s)
        self.assertIn('string recoveryFolder = BackupBeforeAssetMutation();', s)
        self.assertLess(
            s.index('string recoveryFolder = BackupBeforeAssetMutation();'),
            s.index('GameObject physical = new GameObject(')
        )
        self.assertIn('colliderGuid == OriginalColliderGuid', s)
        self.assertIn('colliderLocalId == OriginalColliderLocalId', s)
        self.assertIn('persistent-proxy-world-pose-drift', s)
        self.assertIn('last-lod-silhouette-world-pose-drift', s)
        self.assertIn('last-lod-material-identity-changed', s)
        self.assertIn('proxyRenderer.sharedMaterials', s)
        self.assertIn('authoredFar.sharedMaterials', s)
        self.assertNotIn('originalMesh.sharedMesh == proxy.sharedMesh', s)
        self.assertIn('ForestLookout_watchtower_01', s)
        self.assertIn('Aaris_ForestLookout_Detail.unity', s)
        self.assertIn('Check(district.transform.childCount == 1', s)
        self.assertIn('Check(matches == 1,', s)
        self.assertIn('EditorSceneManager.NewScene(', s)
        self.assertIn('SceneManager.MoveGameObjectToScene(c.tower, newCell);', s)
        self.assertNotIn('DestroyImmediate(c.tower)', s)

    def test_authored_world_has_exact_single_landmark_collider_authority(self):
        """Accept either the untouched source or the natively migrated pilot."""
        source = ROOT / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"
        chunk = ROOT / "Assets/AarisWorld/Scenes/Chunks/Aaris_ForestLookout_Detail.unity"
        scene = WorldScene(source.read_text())
        district = scene.find_unique("ForestLookout")
        if chunk.is_file():
            self.assertEqual(len(scene.descendants(district)), 3)
            proxy = scene.find_unique("__AarisForestLookout_PersistentCover")
            far = scene.find_unique("__AarisForestLookout_FarSilhouette")
            self.assertEqual(scene.children[district], [proxy])
            self.assertEqual(scene.children[proxy], [far])
            self.assertEqual(scene.component_types[proxy], {64, 114})
            self.assertEqual(scene.component_types[far], {33, 23})
            detail = WorldScene(chunk.read_text())
            tower = detail.find_unique("ForestLookout_watchtower_01")
            self.assertEqual(len(detail.descendants(tower)), 3)
            self.assertEqual(detail.component_types[tower], {33, 23, 205})
            self.assertTrue(all(detail.component_types[c] == {33, 23}
                                for c in detail.children[tower]))
            source_for_lod = detail
            collider_owner = proxy
            meta = (chunk.parent / (chunk.name + ".meta")).read_text()
            build_settings = (ROOT / "ProjectSettings/EditorBuildSettings.asset").read_text()
            import re
            guid = re.search(r"(?m)^guid: ([0-9a-f]{32})$", meta)
            self.assertIsNotNone(guid)
            self.assertIn(guid.group(1), build_settings)
            self.assertEqual(build_settings.count(str(chunk.relative_to(ROOT))), 1)
        else:
            tower = scene.find_unique("ForestLookout_watchtower_01")
            self.assertEqual(scene.children[district], [tower])
            self.assertEqual(len(scene.children[tower]), 2)
            self.assertEqual(len(scene.descendants(district)), 4)
            self.assertEqual(scene.component_types[tower], {33, 23, 64, 205})
            self.assertTrue(all(scene.component_types[c] == {33, 23}
                                for c in scene.children[tower]))
            source_for_lod = scene
            collider_owner = tower

        outgoing, incoming = scene.semantic_cross_refs(scene.descendants(district))
        self.assertEqual(outgoing, set())
        self.assertEqual(len(incoming), 1)
        source_component, destination_component = next(iter(incoming))
        self.assertEqual(scene.names[scene.owner[source_component]], "MobileRuntime")
        self.assertEqual(scene.blocks[source_component].kind, 114)
        self.assertEqual(scene.blocks[destination_component].kind, 64)
        self.assertEqual(scene.owner[destination_component], collider_owner)
        collider_yaml = scene.blocks[destination_component].body
        self.assertIn(
            "m_Mesh: {fileID: 4300000, "
            "guid: 6d0fe0e15b4c57e9ba9d921e437cc280",
            collider_yaml,
        )
        lod_components = [
            uid for uid, block in source_for_lod.blocks.items()
            if block.kind == 205 and source_for_lod.owner.get(uid) == tower
        ]
        self.assertEqual(len(lod_components), 1)
        self.assertEqual(
            source_for_lod.blocks[lod_components[0]].body.count("screenRelativeHeight:"),
            3,
        )

    def test_physics_bounds_are_never_discarded_or_approximated(self):
        s = EDITOR.read_text()
        for token in (
            'equivalent.sharedMesh = c.originalCollider.sharedMesh;',
            'equivalent.sharedMaterial = c.originalCollider.sharedMaterial;',
            'equivalent.cookingOptions = c.originalCollider.cookingOptions;',
            'equivalent.contactOffset = c.originalCollider.contactOffset;',
            'obstacleSlot.objectReferenceValue = equivalent;',
            'UnityEngine.Object.DestroyImmediate(c.originalCollider);',
        ):
            self.assertIn(token, s)
        self.assertLess(s.index('obstacleSlot.objectReferenceValue = equivalent;'),
                        s.index('UnityEngine.Object.DestroyImmediate(c.originalCollider);'))

    def test_latest_main_thermal_scheduler_does_not_block_urgent_chunk(self):
        # The OS thermal governor landed after this native pilot was authored.
        # A visible, player-near detail cell must retain native urgency.
        gate = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileStreamingFrameGate.cs").read_text()
        streamer = (ROOT / "Assets/AarisWorld/Scripts/AarisAdditiveChunkStreamer.cs").read_text()
        self.assertIn("internal static bool TryClaimUrgentSceneLoad()", gate)
        self.assertIn("return TryClaimOptionalModelBuild(true);", gate)
        self.assertIn("if (!urgentScene && thermalGap > 0", gate)
        self.assertIn("loadIsUrgent = distance <= entrySquared;", streamer)
        self.assertIn("if (!AarisMobileStreamingFrameGate.TryClaimUrgentSceneLoad()) return;", streamer)
        self.assertIn("AarisAndroidThermalGovernor.SuspendSpeculativePrefetch", streamer)
        self.assertIn("AarisAndroidThermalGovernor.ModerateThermalPressure", streamer)

    def test_last_lod_is_restored_with_no_render_quality_tuning(self):
        s = EDITOR.read_text()
        p = RUNTIME.read_text()
        self.assertIn('LOD[] lods = group.GetLODs();', s)
        self.assertIn('GameObject silhouette = UnityEngine.Object.Instantiate(', s)
        self.assertIn('silhouetteRenderers', p)
        self.assertIn('SceneManager.sceneLoaded += HandleSceneLoaded;', p)
        self.assertIn('SceneManager.sceneUnloaded += HandleSceneUnloaded;', p)
        self.assertIn('ShowSilhouette(registeredChunk.LoadedHandles.Count == 0);', p)
        self.assertIn('loadedScenes.TryGetValue(handle, out ChunkVisuals chunk)', p)
        self.assertNotIn('void Update()', p)
        for forbidden in ('QualitySettings.', 'TerrainData.', 'renderScale',
                          'treeDistance', 'allowSceneActivation = false'):
            self.assertNotIn(forbidden, s + p)


if __name__ == "__main__":
    unittest.main(verbosity=2)
