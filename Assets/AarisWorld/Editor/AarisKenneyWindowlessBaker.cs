#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview.EditorTools
{
    /// <summary>
    /// One-shot authoring operation, NEVER part of gameplay. Replaces only
    /// individually named residential native scene meshes with low-triangle,
    /// single-material CC0 houses. Leaves roads, land, yards and foundations
    /// untouched. The entrance is an actual empty gap with box-collider walls.
    /// </summary>
    public static class AarisKenneyWindowlessBaker
    {
        private const string ScenePath =
            "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const string AssetRoot =
            "Assets/AarisWorld/Models/KenneyWindowlessHouses";
        private const string Marker =
            "/tmp/aaris-codespaces/kenney-windowless.request";
        private const string Result =
            "/tmp/aaris-codespaces/kenney-windowless-result.txt";
        private const string ChildName = "__AarisKenneyWindowlessPrefab";
        private static readonly string[] Styles =
        {
            "bungalow_house", "pitched_roof_cottage", "a_frame_cabin",
            "detached_two_storey_house", "garage_apartment",
            "two_bedroom_garage_house", "row_three_townhouses"
        };
        private static readonly Regex ResidentialName = new Regex(
            @"^.+_(?:bungalow_house|pitched_roof_cottage|a_frame_cabin|" +
            @"detached_two_storey_house|garage_apartment|" +
            @"two_bedroom_garage_house|row_three_townhouses)_\d{2}$",
            RegexOptions.CultureInvariant);
        private static bool busy;
        private static bool previewInMemory;

        [InitializeOnLoadMethod]
        private static void Attach()
        {
            EditorApplication.update -= WatchRequest;
            EditorApplication.update += WatchRequest;
        }

        private static void WatchRequest()
        {
            if (busy || !File.Exists(Marker) || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            busy = true;
            try
            {
                string request = File.ReadAllText(Marker).Trim();
                File.Delete(Marker);
                Bake(request == "apply");
            }
            catch (Exception ex)
            {
                WriteResult("AARIS_KENNEY_BAKE_FAILED " + ex);
                Debug.LogException(ex);
            }
            finally { busy = false; }
        }

        [MenuItem("Aaris/Houses/Preview one CC0 windowless house")]
        private static void Preview() { Bake(false); }

        [MenuItem("Aaris/Houses/Bake CC0 windowless houses into native map")]
        private static void Apply() { Bake(true); }

        private static void WriteResult(string message)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Result));
            File.WriteAllText(Result, message);
            Debug.Log(message);
        }

        private static string StyleOf(string name)
        {
            if (!ResidentialName.IsMatch(name)) return null;
            foreach (string style in Styles)
                if (Regex.IsMatch(name, "_" + Regex.Escape(style) + @"_\d{2}$"))
                    return style;
            return null;
        }

        private static GameObject[] Houses(Scene scene)
        {
            return UnityEngine.Object.FindObjectsByType<GameObject>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(o => o.scene == scene && StyleOf(o.name) != null)
                .OrderBy(o => o.name, StringComparer.Ordinal)
                .ToArray();
        }

        private static void AddBox(GameObject go, Vector3 position, Vector3 size)
        {
            BoxCollider c = go.AddComponent<BoxCollider>();
            c.center = position;
            c.size = size;
        }

        private static GameObject EnsurePrefab(string style, Material shared)
        {
            string folder = AssetRoot + "/Prefabs";
            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(AssetRoot, "Prefabs");
            string outPath = folder + "/AarisWindowless_" + style + ".prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(outPath);
            if (existing != null) return existing;
            string objPath = AssetRoot + "/AarisWindowless_" + style + ".obj";
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(objPath);
            if (model == null)
                throw new InvalidOperationException("Import the Kenney OBJ first: " + objPath);
            MeshFilter filter = model.GetComponentsInChildren<MeshFilter>(true)
                .FirstOrDefault(f => f.sharedMesh != null);
            if (filter == null)
                throw new InvalidOperationException("Kenney OBJ mesh not imported: " + objPath);

            GameObject home = new GameObject("AarisWindowless_" + style);
            home.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            MeshRenderer renderer = home.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = shared;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            renderer.receiveShadows = true;

            float height;
            if (style == "bungalow_house") height = 4.5f;
            else if (style == "pitched_roof_cottage") height = 4.3f;
            else if (style == "a_frame_cabin") height = 4.1f;
            else if (style == "detached_two_storey_house") height = 6.4f;
            else if (style == "garage_apartment") height = 6.1f;
            else if (style == "two_bedroom_garage_house") height = 5.2f;
            else height = 6.2f;
            const float w = 10f, d = 8f, thick = 0.22f, gap = 1.12f;
            const float entryHeight = 2.62f;
            AddBox(home, new Vector3(0, -0.075f, 0),
                new Vector3(w, 0.15f, d));
            AddBox(home, new Vector3(0, height/2, d/2-thick/2),
                new Vector3(w, height, thick));
            AddBox(home, new Vector3(-w/2+thick/2, height/2, 0),
                new Vector3(thick, height, d));
            AddBox(home, new Vector3(w/2-thick/2, height/2, 0),
                new Vector3(thick, height, d));
            float sideWidth = w/2-gap;
            AddBox(home, new Vector3((-w/2-gap)/2, height/2, -d/2+thick/2),
                new Vector3(sideWidth, height, thick));
            AddBox(home, new Vector3((w/2+gap)/2, height/2, -d/2+thick/2),
                new Vector3(sideWidth, height, thick));
            AddBox(home, new Vector3(0, (height+entryHeight)/2, -d/2+thick/2),
                new Vector3(gap*2, height-entryHeight, thick));
            // Roof stays passable from inside only up to the eave; a simple
            // box collider avoids expensive per-triangle physics.
            AddBox(home, new Vector3(0, height+0.07f, 0),
                new Vector3(w+0.70f, 0.18f, d+0.70f));
            AddBox(home, new Vector3(0, -0.09f, -d/2-0.27f),
                new Vector3(3.10f, 0.11f, 0.55f));

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(home, outPath);
            UnityEngine.Object.DestroyImmediate(home);
            if (prefab == null)
                throw new InvalidOperationException("Failed to save prefab " + outPath);
            return prefab;
        }

        private static Material EnsureMaterial()
        {
            string path = AssetRoot + "/KenneyWindowlessURP.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                throw new InvalidOperationException("URP/Lit shader unavailable");
            Texture2D atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(
                AssetRoot + "/colormap.png");
            if (atlas == null)
                throw new InvalidOperationException("Kenney CC0 atlas missing");
            Material m = new Material(shader) { name = "Kenney Windowless Houses URP" };
            m.SetTexture("_BaseMap", atlas);
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0.16f);
            m.SetFloat("_Metallic", 0f);
            m.enableInstancing = true;
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        private static bool Replace(GameObject source, GameObject prefab)
        {
            if (source.transform.Find(ChildName) != null) return false;
            var filter = source.GetComponent<MeshFilter>();
            var renderer = source.GetComponent<MeshRenderer>();
            if (filter == null || filter.sharedMesh == null || renderer == null)
                throw new InvalidOperationException("House missing base native mesh: " + source.name);
            foreach (Transform child in source.transform)
                if (!Regex.IsMatch(child.name, @"_LOD[12]$"))
                    throw new InvalidOperationException(
                        "Unexpected authored child; refusing to delete: " + source.name +
                        " / " + child.name);

            Bounds old = filter.sharedMesh.bounds;
            if (old.size.x < 3 || old.size.y < 2 || old.size.z < 3 ||
                old.size.x > 50 || old.size.y > 50 || old.size.z > 50)
                throw new InvalidOperationException("Invalid source dimensions " + source.name);
            var prefabFilter = prefab.GetComponent<MeshFilter>();
            Bounds target = prefabFilter.sharedMesh.bounds;
            if (target.size.x < 1 || target.size.y < 1 || target.size.z < 1)
                throw new InvalidOperationException("Invalid CC0 mesh dimensions");
            Vector3 size = new Vector3(
                old.size.x / target.size.x,
                old.size.y / target.size.y,
                old.size.z / target.size.z);
            // Match existing authored width, height, depth, root rotation, scale
            // and ground position. Roof overhang remains inside old mesh bounds.
            Vector3 offset = new Vector3(
                old.center.x - target.center.x*size.x,
                old.min.y - target.min.y*size.y,
                old.center.z - target.center.z*size.z);

            var instance = PrefabUtility.InstantiatePrefab(prefab, source.scene) as GameObject;
            if (instance == null) throw new InvalidOperationException("Prefab instantiate failed");
            Undo.RegisterCreatedObjectUndo(instance, "Replace Aaris house with Kenney house");
            instance.name = ChildName;
            instance.transform.SetParent(source.transform, false);
            instance.transform.localPosition = offset;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = size;

            foreach (Transform child in source.transform.Cast<Transform>().ToArray())
                if (child != instance.transform && Regex.IsMatch(child.name, @"_LOD[12]$"))
                    Undo.DestroyObjectImmediate(child.gameObject);
            LODGroup lod = source.GetComponent<LODGroup>();
            if (lod != null) Undo.DestroyObjectImmediate(lod);
            MeshCollider collision = source.GetComponent<MeshCollider>();
            if (collision != null) Undo.DestroyObjectImmediate(collision);
            Undo.DestroyObjectImmediate(renderer);
            Undo.DestroyObjectImmediate(filter);
            EditorUtility.SetDirty(source);
            return true;
        }

        private static void Bake(bool applyAll)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
                throw new InvalidOperationException("Wrong scene; load native TerrainDemoScene first");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Cannot edit scene during Play Mode");
            if (scene.isDirty)
                throw new InvalidOperationException(
                    "Scene has unsaved Editor changes, including a previous preview. " +
                    "Reopen the saved scene ONLY after checking those changes; " +
                    "refusing to save unrelated work");
            GameObject[] houses = Houses(scene);
            if (houses.Length < 40 || houses.Length > 90)
                throw new InvalidOperationException(
                    "Unexpected residential house count " + houses.Length);
            Material shared = EnsureMaterial();
            var prefabs = Styles.ToDictionary(style => style,
                style => EnsurePrefab(style, shared), StringComparer.Ordinal);
            AssetDatabase.SaveAssets();

            var work = applyAll ? houses :
                houses.Where(o => o.name == "ClockTowerTown_bungalow_house_03")
                    .Take(1).ToArray();
            if (work.Length == 0)
                throw new InvalidOperationException("Preview target not found");
            if (applyAll)
            {
                Directory.CreateDirectory("/tmp/aaris-codespaces");
                string backupPath = "/tmp/aaris-codespaces/windowless-scene-pre-bake-" +
                    DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + ".unity";
                File.Copy(Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                    scene.path), backupPath, false);
                Debug.Log("AARIS_KENNEY_PRE_BAKE_BACKUP " + backupPath);
            }
            int updated = 0, already = 0;
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Aaris Windowless House Native Scene Replacement");
            try
            {
                foreach (var home in work)
                {
                    if (Replace(home, prefabs[StyleOf(home.name)]))
                        updated++;
                    else already++;
                }
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
            Undo.CollapseUndoOperations(group);
            if (applyAll)
            {
                if (!EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException("Unity refused native scene save");
                previewInMemory = false;
            }
            else
            {
                previewInMemory = true;
                Selection.activeGameObject = work[0];
                if (SceneView.lastActiveSceneView != null)
                {
                    SceneView.lastActiveSceneView.LookAt(
                        work[0].transform.position + Vector3.up * 2.0f,
                        Quaternion.Euler(18, 140, 0), 18);
                    SceneView.lastActiveSceneView.Repaint();
                }
            }
            WriteResult("AARIS_KENNEY_WINDOWLESS_" +
                (applyAll ? "SCENE_SAVED" : "PREVIEW_READY") +
                " houses_found=" + houses.Length + " updated=" + updated +
                " already=" + already + " single_material=true" +
                " authored_prefabs=7 open_entry=true window_panels=0 door_panels=0");
        }
    }
}
#endif
