#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview.Editor
{
    /// <summary>Authors the mobile preset on Terrain renderer components only.
    /// Native height, splat paint, detail/tree instances and colliders stay intact.</summary>
    public static class AarisUltraTerrainBudget
    {
        private const string Main = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        [Serializable] private sealed class Row
        {
            public string terrain, dataGuid;
            public int trees, layers, fullLodBefore, fullLodAfter;
            public bool drawInstanced;
            public string terrainShader;
            public string[] treePrototypes;
            public float basemapBefore, basemapAfter, detailBefore, detailAfter;
            public float billboardBefore, billboardAfter, treeReachBefore, treeReachAfter;
            public float pixelErrorBefore, pixelErrorAfter;
        }
        [Serializable] private sealed class Result
        { public string status; public bool nativeTerrainDataUnchanged; public Row[] terrain; }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        [MenuItem("Aaris/World/Apply Ultra mobile terrain renderer budget")]
        public static void Apply()
        {
            if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("TERRAIN_BUDGET_DEDICATED_BATCH_ONLY");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty)
                    throw new InvalidOperationException("TERRAIN_BUDGET_UNSAVED_SCENE");
            Scene scene = EditorSceneManager.OpenScene(Main, OpenSceneMode.Single);
            Terrain[] terrains = UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None);
            if (terrains.Length != 4) throw new InvalidOperationException("TERRAIN_BUDGET_EXPECT_FOUR_NATIVE_TILES");
            var rows = new List<Row>(4);
            var originalBytes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Terrain tile in terrains)
            {
                TerrainData data = tile.terrainData;
                string path = AssetDatabase.GetAssetPath(data);
                if (tile.gameObject.scene != scene || data == null || string.IsNullOrEmpty(path))
                    throw new InvalidOperationException("TERRAIN_BUDGET_NONPERSISTENT_TILE");
                originalBytes[path] = HashFile(path);
                var row = new Row { terrain = tile.name, dataGuid = AssetDatabase.AssetPathToGUID(path),
                    trees = data.treeInstanceCount, layers = data.terrainLayers.Length,
                    basemapBefore = tile.basemapDistance, detailBefore = tile.detailObjectDistance,
                    billboardBefore = tile.treeBillboardDistance, treeReachBefore = tile.treeDistance,
                    pixelErrorBefore = tile.heightmapPixelError,
                    fullLodBefore = tile.treeMaximumFullLODCount,
                    drawInstanced = tile.drawInstanced,
                    terrainShader = tile.materialTemplate != null && tile.materialTemplate.shader != null
                        ? tile.materialTemplate.shader.name : "unavailable" };
                var prototypes = new List<string>();
                foreach (TreePrototype prototype in data.treePrototypes)
                {
                    GameObject prefab = prototype.prefab;
                    prototypes.Add(prefab == null ? "null" : prefab.name + " guid=" +
                        AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab)) +
                        " lodGroups=" + prefab.GetComponentsInChildren<LODGroup>(true).Length +
                        " treeComponents=" + prefab.GetComponentsInChildren<Tree>(true).Length +
                        " billboardRenderers=" + prefab.GetComponentsInChildren<BillboardRenderer>(true).Length);
                }
                row.treePrototypes = prototypes.ToArray();
                // Unity's own precomputed terrain basemap covers the aerial
                // view; full authored splat blending remains close to the player.
                tile.basemapDistance = Mathf.Min(tile.basemapDistance, 60f);
                tile.detailObjectDistance = Mathf.Min(tile.detailObjectDistance, 30f);
                tile.treeBillboardDistance = Mathf.Min(tile.treeBillboardDistance, 60f);
                tile.treeDistance = Mathf.Min(tile.treeDistance, 600f);
                tile.treeMaximumFullLODCount = Mathf.Min(tile.treeMaximumFullLODCount, 50);
                // Bound the projected tessellation error for the mobile preset.
                // This changes visual LOD only: native height samples and the
                // full-resolution TerrainCollider remain byte-identical.
                tile.heightmapPixelError = Mathf.Max(tile.heightmapPixelError, 12f);
                row.basemapAfter = tile.basemapDistance; row.detailAfter = tile.detailObjectDistance;
                row.billboardAfter = tile.treeBillboardDistance; row.treeReachAfter = tile.treeDistance;
                row.fullLodAfter = tile.treeMaximumFullLODCount;
                row.pixelErrorAfter = tile.heightmapPixelError;
                rows.Add(row);
            }
            if (!EditorSceneManager.SaveScene(scene, Main))
                throw new InvalidOperationException("TERRAIN_BUDGET_SAVE_FAILED");
            foreach (var item in originalBytes)
            {
                if (HashFile(item.Key) != item.Value)
                    throw new InvalidOperationException("TERRAIN_DATA_BYTES_CHANGED");
            }
            const string output = "/tmp/aaris-ultra60/terrain-budget";
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonUtility.ToJson(new Result {
                status = "passed", nativeTerrainDataUnchanged = true, terrain = rows.ToArray() }, true));
            Debug.Log("AARIS_ULTRA_TERRAIN_BUDGET_PASS tiles=4 native_data_bytes_unchanged=true");
        }
    }
}
#endif
