#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview.Editor
{
    /// <summary>Repackages proven visual-only district roots into native 150m
    /// additive cells. Original scenes, terrain, meshes, physics proxies and
    /// interactive systems remain untouched; old scenes remain source archives.</summary>
    public static class AarisUltraSectorBake
    {
        private const string Main = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const string Folder = "Assets/AarisWorld/Scenes/Chunks/";
        private const string CellPrefix = "Aaris_Cell_";
        private const float CellSize = 150f;
        private const string ReportDirectory = "/tmp/aaris-ultra-sector-bake";
        private sealed class Item
        {
            internal GameObject root;
            internal AarisChunkFallbackVisibility fallback;
            internal Bounds bounds;
            internal Vector3 position, scale;
            internal Quaternion rotation;
            internal string geometryFingerprint, lodFingerprint;
        }
        [Serializable]
        private sealed class RootRecord
        {
            public string scenePath, rootName, geometryFingerprint, lodFingerprint, fallbackIdentity;
            public Vector3 position, scale;
            public Quaternion rotation;
        }
        [Serializable]
        private sealed class CellRecord
        {
            public string scenePath;
            public Vector2 center, halfExtents;
        }
        [Serializable]
        private sealed class ArchiveHash
        {
            public string path, sha256;
        }
        [Serializable]
        private sealed class BakeReport
        {
            public int gridMetres, sourceScenes, newCells, preservedStaticRoots;
            public bool oldSceneBytesUnchanged, persistentCollisionAndTerrainUnchanged,
                meshMaterialReferencesUnchanged, worldPosesUnchanged;
            public string persistentFingerprint;
            public string[] changedPaths, rootEvidence;
            public RootRecord[] roots;
            public CellRecord[] cells;
            public ArchiveHash[] archives;
        }
        [Serializable]
        private sealed class ReloadReport
        {
            public bool passed, savedFilesUnchanged;
            public int cellsReloaded, rootsReloaded, archivesHashed;
            public string persistentFingerprint;
        }
        private sealed class Sector
        {
            internal string path;
            internal Vector2 center;
            internal Vector2 half;
            internal readonly List<Item> items = new List<Item>();
        }
        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("AARIS_ULTRA_SECTOR_UNSAFE " + reason);
        }
        private static string Label(int index) => index < 0 ? "m" + (-index) : "p" + index;

        [MenuItem("Aaris/World/Probe 150m Ultra Sectors")]
        public static void Probe() => Run(false);

        [MenuItem("Aaris/World/Bake 150m Ultra Sectors")]
        public static void Apply() => Run(true);

        [MenuItem("Aaris/World/Verify Saved 150m Ultra Sectors")]
        public static void VerifySaved()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "play-mode-active");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                Require(!SceneManager.GetSceneAt(i).isDirty, "unsaved-scene-work");
            string reportPath = Path.Combine(ReportDirectory, "result.json");
            Require(File.Exists(reportPath), "saved-verification-report-missing");
            var expected = JsonUtility.FromJson<BakeReport>(File.ReadAllText(reportPath));
            Require(expected != null && expected.gridMetres == 150 &&
                expected.newCells > 0 && expected.sourceScenes > 0 &&
                expected.roots != null && expected.cells != null && expected.archives != null &&
                expected.changedPaths != null && !string.IsNullOrEmpty(expected.persistentFingerprint),
                "saved-verification-evidence-incomplete");
            Require(expected.cells.Length == expected.newCells &&
                expected.roots.Length == expected.preservedStaticRoots &&
                expected.archives.Length == expected.sourceScenes * 2,
                "saved-verification-evidence-count-mismatch");

            var unchanged = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in expected.changedPaths)
            {
                Require(File.Exists(path) && !unchanged.ContainsKey(path), "saved-output-missing-or-duplicate");
                unchanged.Add(path, FileHash(path));
            }
            Require(expected.changedPaths.Length == expected.newCells * 2 + 2 &&
                unchanged.ContainsKey(Main) && unchanged.ContainsKey("ProjectSettings/EditorBuildSettings.asset"),
                "saved-output-manifest-count-mismatch");
            var archivePaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (ArchiveHash archive in expected.archives)
            {
                Require(archive != null && File.Exists(archive.path) && archivePaths.Add(archive.path) &&
                    FileHash(archive.path) == archive.sha256, "saved-source-archive-hash-mismatch");
                unchanged.Add(archive.path, archive.sha256);
            }
            var expectedCells = new Dictionary<string, CellRecord>(StringComparer.Ordinal);
            foreach (CellRecord cell in expected.cells)
            {
                Require(cell != null && cell.scenePath.StartsWith(Folder + CellPrefix, StringComparison.Ordinal) &&
                    !expectedCells.ContainsKey(cell.scenePath) && unchanged.ContainsKey(cell.scenePath) &&
                    unchanged.ContainsKey(cell.scenePath + ".meta"), "saved-cell-evidence-duplicate-or-unlisted");
                expectedCells.Add(cell.scenePath, cell);
            }
            var records = new Dictionary<string, List<RootRecord>>(StringComparer.Ordinal);
            foreach (RootRecord record in expected.roots)
            {
                Require(record != null && expectedCells.ContainsKey(record.scenePath), "saved-root-cell-unknown");
                if (!records.TryGetValue(record.scenePath, out List<RootRecord> list))
                { list = new List<RootRecord>(); records.Add(record.scenePath, list); }
                list.Add(record);
            }

            // Reopen from disk before reading any serialized state. No save,
            // import or asset mutation occurs in this verification entry point.
            Scene bootstrap = EditorSceneManager.OpenScene(Main, OpenSceneMode.Single);
            Require(PersistentFingerprint(bootstrap) == expected.persistentFingerprint,
                "saved-persistent-collision-or-terrain-drift");
            AarisAdditiveChunkStreamer streamer = null;
            var fallbacks = new Dictionary<string, AarisChunkFallbackVisibility>(StringComparer.Ordinal);
            foreach (GameObject root in bootstrap.GetRootGameObjects())
            {
                foreach (var found in root.GetComponentsInChildren<AarisAdditiveChunkStreamer>(true))
                { Require(streamer == null, "saved-streamer-duplicate"); streamer = found; }
                foreach (var fallback in root.GetComponentsInChildren<AarisChunkFallbackVisibility>(true))
                    fallbacks.Add(StableIdentity(fallback), fallback);
            }
            Require(streamer != null, "saved-streamer-missing");
            var chunks = new SerializedObject(streamer).FindProperty("chunks");
            Require(chunks != null && chunks.arraySize == expected.newCells, "saved-streamer-cell-count");
            var registered = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < chunks.arraySize; i++)
            {
                var cell = chunks.GetArrayElementAtIndex(i);
                string path = cell.FindPropertyRelative("scenePath").stringValue;
                bool known = expectedCells.TryGetValue(path, out CellRecord planned);
                Require(registered.Add(path) && known,
                    "saved-streamer-cell-unknown-or-duplicate");
                Require(Vector2.Distance(cell.FindPropertyRelative("center").vector2Value, planned.center) < .001f &&
                    Vector2.Distance(cell.FindPropertyRelative("halfExtents").vector2Value, planned.halfExtents) < .001f,
                    "saved-streamer-bounds-drift");
            }
            string[] buildPaths = AarisExplorerBuild.VerifiedBuildScenes();
            var packaged = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < buildPaths.Length; i++) packaged.Add(buildPaths[i]);
            Require(packaged.SetEquals(registered) && registered.SetEquals(expectedCells.Keys),
                "saved-build-streamer-evidence-mismatch");

            int verifiedRoots = 0;
            var matchedFallbacks = new HashSet<string>(StringComparer.Ordinal);
            foreach (CellRecord cell in expected.cells)
            {
                Require(records.TryGetValue(cell.scenePath, out List<RootRecord> planned), "saved-cell-has-no-roots");
                Scene scene = EditorSceneManager.OpenScene(cell.scenePath, OpenSceneMode.Additive);
                try
                {
                    GameObject[] roots = scene.GetRootGameObjects();
                    Require(roots.Length == planned.Count, "saved-root-count-drift-" + cell.scenePath);
                    var consumed = new HashSet<GameObject>();
                    foreach (RootRecord record in planned)
                    {
                        GameObject found = null;
                        foreach (GameObject root in roots)
                        {
                            if (consumed.Contains(root) || root.name != record.rootName ||
                                !SamePose(root.transform, record.position, record.rotation, record.scale)) continue;
                            Require(found == null, "saved-root-identity-ambiguous");
                            found = root;
                        }
                        Require(found != null, "saved-world-pose-or-root-name-drift-" + record.rootName);
                        consumed.Add(found);
                        foreach (Component component in found.GetComponentsInChildren<Component>(true))
                            Require(component is Transform || component is MeshFilter ||
                                component is MeshRenderer || component is LODGroup,
                                "saved-cell-has-physics-gameplay-or-missing-script");
                        Require(GeometryFingerprint(found) == record.geometryFingerprint,
                            "saved-mesh-or-material-reference-drift-" + record.rootName);
                        Require(LodFingerprint(found) == record.lodFingerprint,
                            "saved-lod-threshold-or-renderer-link-drift-" + record.rootName);
                        bool fallbackFound = fallbacks.TryGetValue(record.fallbackIdentity,
                            out AarisChunkFallbackVisibility fallback);
                        Require(matchedFallbacks.Add(record.fallbackIdentity) && fallbackFound,
                            "saved-fallback-identity-missing-or-duplicate");
                        var fallbackData = new SerializedObject(fallback);
                        Require(fallbackData.FindProperty("chunkScenePath").stringValue == cell.scenePath,
                            "saved-fallback-cell-reference-drift");
                        var silhouettes = fallbackData.FindProperty("silhouetteRenderers");
                        Require(silhouettes != null && silhouettes.arraySize == 1,
                            "saved-fallback-renderer-reference-missing");
                        var silhouette = silhouettes.GetArrayElementAtIndex(0).objectReferenceValue as MeshRenderer;
                        var levels = found.GetComponent<LODGroup>().GetLODs();
                        var last = levels[levels.Length - 1].renderers[0] as MeshRenderer;
                        var silhouetteMesh = silhouette != null ? silhouette.GetComponent<MeshFilter>() : null;
                        var lastMesh = last != null ? last.GetComponent<MeshFilter>() : null;
                        Require(silhouetteMesh != null && lastMesh != null &&
                            silhouetteMesh.sharedMesh == lastMesh.sharedMesh &&
                            SamePose(silhouette.transform, last.transform.position,
                                last.transform.rotation, last.transform.lossyScale),
                            "saved-fallback-mesh-or-world-pose-drift");
                        verifiedRoots++;
                    }
                }
                finally { EditorSceneManager.CloseScene(scene, true); }
            }
            Require(verifiedRoots == expected.preservedStaticRoots, "saved-total-root-count-drift");
            Require(PersistentFingerprint(bootstrap) == expected.persistentFingerprint,
                "saved-persistent-state-changed-during-verification");
            foreach (var entry in unchanged)
                Require(FileHash(entry.Key) == entry.Value, "verification-wrote-project-file");
            var report = new ReloadReport { passed = true, savedFilesUnchanged = true,
                cellsReloaded = expected.newCells, rootsReloaded = verifiedRoots,
                archivesHashed = expected.archives.Length, persistentFingerprint = expected.persistentFingerprint };
            File.WriteAllText(Path.Combine(ReportDirectory, "saved-verification.json"), JsonUtility.ToJson(report, true));
            Debug.Log("AARIS_ULTRA_SECTOR_SAVED_VERIFY_PASS cells=" + expected.newCells +
                " roots=" + verifiedRoots + " archives=" + expected.archives.Length);
        }

        private static bool SamePose(Transform transform, Vector3 position, Quaternion rotation, Vector3 scale) =>
            Vector3.Distance(transform.position, position) < .001f &&
            Quaternion.Angle(transform.rotation, rotation) < .01f &&
            Vector3.Distance(transform.lossyScale, scale) < .001f;

        private static void Run(bool apply)
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "play-mode-active");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                Require(!SceneManager.GetSceneAt(i).isDirty, "unsaved-scene-work");
            Scene bootstrap = EditorSceneManager.OpenScene(Main, OpenSceneMode.Single);
            var mobile = GameObject.Find("MobileRuntime");
            Require(mobile != null, "mobile-runtime-missing");
            var streamer = mobile.GetComponent<AarisAdditiveChunkStreamer>();
            Require(streamer != null, "native-streamer-missing");
            var config = new SerializedObject(streamer);
            var chunks = config.FindProperty("chunks");
            Require(chunks != null && chunks.arraySize > 0, "no-native-chunks");
            var oldPaths = new HashSet<string>(StringComparer.Ordinal);
            var originalSceneHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < chunks.arraySize; i++)
            {
                string path = chunks.GetArrayElementAtIndex(i).FindPropertyRelative("scenePath").stringValue;
                Require(path.StartsWith(Folder, StringComparison.Ordinal) &&
                    !Path.GetFileName(path).StartsWith(CellPrefix, StringComparison.Ordinal),
                    "already-baked-or-unknown-chunk");
                Require(File.Exists(path) && oldPaths.Add(path), "missing-or-duplicate-chunk");
                originalSceneHashes[path] = FileHash(path);
                Require(File.Exists(path + ".meta"), "source-scene-meta-missing");
                originalSceneHashes[path + ".meta"] = FileHash(path + ".meta");
            }
            string persistentBefore = PersistentFingerprint(bootstrap);
            var fallbacks = UnityEngine.Object.FindObjectsByType<AarisChunkFallbackVisibility>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var usedFallbacks = new HashSet<AarisChunkFallbackVisibility>();
            var sectors = new SortedDictionary<string, Sector>(StringComparer.Ordinal);
            var opened = new List<Scene>();
            int moved = 0;
            foreach (string path in oldPaths)
            {
                Scene detail = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                opened.Add(detail);
                foreach (GameObject root in detail.GetRootGameObjects())
                {
                    var lod = root.GetComponent<LODGroup>();
                    Require(lod != null && root.transform.parent == null, "non-lod-root-" + root.name);
                    foreach (Component c in root.GetComponentsInChildren<Component>(true))
                        Require(c is Transform || c is MeshFilter || c is MeshRenderer || c is LODGroup,
                            "gameplay-or-collider-in-detail-" + root.name);
                    LOD[] levels = lod.GetLODs();
                    Require(levels.Length >= 2 && levels[levels.Length - 1].renderers.Length == 1,
                        "missing-last-lod-" + root.name);
                    var last = levels[levels.Length - 1].renderers[0] as MeshRenderer;
                    Require(last != null && last.GetComponent<MeshFilter>() != null,
                        "non-mesh-last-lod-" + root.name);
                    Mesh farMesh = last.GetComponent<MeshFilter>().sharedMesh;
                    AarisChunkFallbackVisibility matched = null;
                    foreach (var fallback in fallbacks)
                    {
                        if (fallback == null || fallback.gameObject.scene != bootstrap || usedFallbacks.Contains(fallback)) continue;
                        var data = new SerializedObject(fallback);
                        if (data.FindProperty("chunkScenePath").stringValue != path) continue;
                        var references = data.FindProperty("silhouetteRenderers");
                        if (references.arraySize != 1) continue;
                        var renderer = references.GetArrayElementAtIndex(0).objectReferenceValue as MeshRenderer;
                        if (renderer == null || renderer.GetComponent<MeshFilter>() == null ||
                            renderer.GetComponent<MeshFilter>().sharedMesh != farMesh ||
                            Vector3.Distance(renderer.transform.position, last.transform.position) > .001f ||
                            Quaternion.Angle(renderer.transform.rotation, last.transform.rotation) > .01f ||
                            Vector3.Distance(renderer.transform.lossyScale, last.transform.lossyScale) > .001f) continue;
                        Require(matched == null, "ambiguous-fallback-" + root.name);
                        matched = fallback;
                    }
                    Require(matched != null, "persistent-fallback-not-found-" + root.name);
                    usedFallbacks.Add(matched);
                    Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
                    Require(renderers.Length > 0, "root-has-no-renderers");
                    Bounds bounds = renderers[0].bounds;
                    for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
                    int x = Mathf.FloorToInt(bounds.center.x / CellSize);
                    int z = Mathf.FloorToInt(bounds.center.z / CellSize);
                    string destination = Folder + CellPrefix + Label(x) + "_" + Label(z) + "_Detail.unity";
                    Require(!File.Exists(destination), "destination-already-exists-" + destination);
                    if (!sectors.TryGetValue(destination, out Sector sector))
                    {
                        sector = new Sector { path = destination,
                            center = new Vector2((x + .5f) * CellSize, (z + .5f) * CellSize),
                            half = new Vector2(CellSize * .5f, CellSize * .5f) };
                        sectors.Add(destination, sector);
                    }
                    sector.half.x = Mathf.Max(sector.half.x,
                        Mathf.Abs(bounds.center.x - sector.center.x) + bounds.extents.x + 1f);
                    sector.half.y = Mathf.Max(sector.half.y,
                        Mathf.Abs(bounds.center.z - sector.center.y) + bounds.extents.z + 1f);
                    sector.items.Add(new Item { root = root, fallback = matched, bounds = bounds,
                        position = root.transform.position, rotation = root.transform.rotation,
                        scale = root.transform.lossyScale, geometryFingerprint = GeometryFingerprint(root),
                        lodFingerprint = LodFingerprint(root) });
                    moved++;
                }
            }
            Require(moved > 0 && sectors.Count > 0, "empty-partition");
            Debug.Log("AARIS_ULTRA_SECTOR_PROBE_PASS old_scenes=" + oldPaths.Count +
                " grid_m=" + CellSize + " nonempty_cells=" + sectors.Count + " static_roots=" + moved);
            if (!apply)
            {
                foreach (Scene scene in opened) EditorSceneManager.CloseScene(scene, true);
                return;
            }
            // User-authorized action is still refused while any open source
            // scene contains unsaved work; inspection above opens disk snapshots.
            string backup = Path.Combine("/tmp", "aaris-ultra-sector-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            File.Copy(Main, Path.Combine(backup, "main.unity"));
            File.Copy("ProjectSettings/EditorBuildSettings.asset", Path.Combine(backup, "EditorBuildSettings.asset"));
            var builds = new List<EditorBuildSettingsScene>();
            foreach (var entry in EditorBuildSettings.scenes)
                if (!oldPaths.Contains(entry.path)) builds.Add(entry);
            chunks.arraySize = sectors.Count;
            int ordinal = 0;
            var changedPaths = new List<string> { Main, "ProjectSettings/EditorBuildSettings.asset" };
            var rootEvidence = new List<string>();
            var rootRecords = new List<RootRecord>();
            var cellRecords = new List<CellRecord>();
            foreach (Sector sector in sectors.Values)
            {
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                cellRecords.Add(new CellRecord { scenePath = sector.path,
                    center = sector.center, halfExtents = sector.half });
                foreach (Item item in sector.items)
                {
                    SceneManager.MoveGameObjectToScene(item.root, scene);
                    Require(Vector3.Distance(item.position, item.root.transform.position) < .001f &&
                        Quaternion.Angle(item.rotation, item.root.transform.rotation) < .01f &&
                        Vector3.Distance(item.scale, item.root.transform.lossyScale) < .001f,
                        "world-transform-drift");
                    Require(item.geometryFingerprint == GeometryFingerprint(item.root), "mesh-or-material-reference-drift");
                    Require(item.lodFingerprint == LodFingerprint(item.root), "lod-threshold-or-renderer-link-drift");
                    rootRecords.Add(new RootRecord { scenePath = sector.path, rootName = item.root.name,
                        position = item.position, rotation = item.rotation, scale = item.scale,
                        geometryFingerprint = item.geometryFingerprint, lodFingerprint = item.lodFingerprint,
                        fallbackIdentity = StableIdentity(item.fallback) });
                    rootEvidence.Add(sector.path + " | " + item.root.name + " | geometry_sha256=" +
                        item.geometryFingerprint + " | position=" + item.position.ToString("R") +
                        " | rotation=" + item.rotation.ToString("R") + " | scale=" + item.scale.ToString("R"));
                    foreach (MeshRenderer renderer in item.root.GetComponentsInChildren<MeshRenderer>(true))
                        GameObjectUtility.SetStaticEditorFlags(renderer.gameObject,
                            GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) | StaticEditorFlags.BatchingStatic);
                    var fallback = new SerializedObject(item.fallback);
                    fallback.FindProperty("chunkScenePath").stringValue = sector.path;
                    fallback.ApplyModifiedPropertiesWithoutUndo();
                }
                var cell = chunks.GetArrayElementAtIndex(ordinal++);
                cell.FindPropertyRelative("scenePath").stringValue = sector.path;
                cell.FindPropertyRelative("center").vector2Value = sector.center;
                cell.FindPropertyRelative("halfExtents").vector2Value = sector.half;
                Require(EditorSceneManager.SaveScene(scene, sector.path), "sector-save-failed");
                changedPaths.Add(sector.path);
                changedPaths.Add(sector.path + ".meta");
                builds.Add(new EditorBuildSettingsScene(sector.path, true));
                SceneManager.SetActiveScene(bootstrap);
                EditorSceneManager.CloseScene(scene, true);
            }
            config.ApplyModifiedPropertiesWithoutUndo();
            Require(EditorSceneManager.SaveScene(bootstrap, Main), "bootstrap-save-failed");
            EditorBuildSettings.scenes = builds.ToArray();
            foreach (Scene scene in opened) EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.Refresh();
            foreach (var original in originalSceneHashes)
                Require(FileHash(original.Key) == original.Value, "archived-source-scene-changed");
            Require(persistentBefore == PersistentFingerprint(bootstrap), "persistent-physics-or-terrain-changed");
            var archives = new List<ArchiveHash>();
            foreach (var original in originalSceneHashes)
                archives.Add(new ArchiveHash { path = original.Key, sha256 = original.Value });
            archives.Sort((a, b) => StringComparer.Ordinal.Compare(a.path, b.path));
            var report = new BakeReport { gridMetres = 150, sourceScenes = oldPaths.Count,
                newCells = sectors.Count, preservedStaticRoots = moved,
                oldSceneBytesUnchanged = true, persistentCollisionAndTerrainUnchanged = true,
                meshMaterialReferencesUnchanged = true, worldPosesUnchanged = true,
                changedPaths = changedPaths.ToArray(), rootEvidence = rootEvidence.ToArray(),
                persistentFingerprint = persistentBefore, roots = rootRecords.ToArray(),
                cells = cellRecords.ToArray(), archives = archives.ToArray() };
            Directory.CreateDirectory(ReportDirectory);
            File.WriteAllText(Path.Combine(ReportDirectory, "result.json"), JsonUtility.ToJson(report, true));
            File.WriteAllLines(Path.Combine(ReportDirectory, "changed-paths.txt"), report.changedPaths);
            Debug.Log("AARIS_ULTRA_SECTOR_APPLY_PASS grid_m=" + CellSize +
                " cells=" + sectors.Count + " roots=" + moved + " source_archive_scenes=" + oldPaths.Count +
                " backup=" + backup);
        }

        private static string FileHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static string Fingerprint(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .Replace("-", "").ToLowerInvariant();
        }

        private static string AssetIdentity(UnityEngine.Object asset)
        {
            if (asset == null) return "null";
            Require(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long id),
                "non-persistent-geometry-resource");
            return guid + ":" + id;
        }

        private static string StableIdentity(UnityEngine.Object value)
        {
            if (value == null) return "null";
            return EditorUtility.IsPersistent(value) ? AssetIdentity(value) :
                GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();
        }

        private static string HierarchyKey(Transform value, Transform root)
        {
            Require(value != null && value.IsChildOf(root), "renderer-reference-outside-static-root");
            var parts = new List<string>();
            for (Transform item = value; item != root; item = item.parent)
                parts.Add(item.GetSiblingIndex() + ":" + item.name.Length + ":" + item.name);
            parts.Add(root.name.Length + ":" + root.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string Scalar(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string VectorIdentity(Vector3 value) =>
            Scalar(value.x) + "," + Scalar(value.y) + "," + Scalar(value.z);

        private static string MatrixIdentity(Matrix4x4 value)
        {
            var text = new StringBuilder();
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    text.Append(Scalar(value[row, column])).Append(',');
            return text.ToString();
        }

        private static string LodFingerprint(GameObject root)
        {
            var value = new StringBuilder();
            LODGroup[] groups = root.GetComponentsInChildren<LODGroup>(true);
            Require(groups.Length != 0, "static-root-lod-missing");
            foreach (LODGroup group in groups)
            {
                value.Append(HierarchyKey(group.transform, root.transform)).Append('|')
                    .Append(group.enabled).Append('|').Append((int)group.fadeMode).Append('|')
                    .Append(group.animateCrossFading).Append('|').Append(Scalar(group.size)).Append('|')
                    .Append(VectorIdentity(group.localReferencePoint)).Append('\n');
                LOD[] levels = group.GetLODs();
                Require(levels.Length >= 2, "static-root-lod-levels-missing");
                for (int i = 0; i < levels.Length; i++)
                {
                    value.Append(i).Append('|').Append(Scalar(levels[i].screenRelativeTransitionHeight))
                        .Append('|').Append(Scalar(levels[i].fadeTransitionWidth));
                    foreach (Renderer renderer in levels[i].renderers)
                    {
                        Require(renderer != null, "lod-renderer-link-missing");
                        value.Append('|').Append(HierarchyKey(renderer.transform, root.transform));
                    }
                    value.Append('\n');
                }
            }
            return Fingerprint(value.ToString());
        }

        private static string GeometryFingerprint(GameObject root)
        {
            var value = new StringBuilder();
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = renderer.GetComponent<MeshFilter>();
                Require(filter != null, "mesh-filter-missing");
                value.Append(HierarchyKey(renderer.transform, root.transform)).Append('|')
                    .Append(AssetIdentity(filter.sharedMesh));
                foreach (Material material in renderer.sharedMaterials)
                    value.Append('|').Append(AssetIdentity(material));
                value.Append('\n');
            }
            return Fingerprint(value.ToString());
        }

        private static string PersistentFingerprint(Scene scene)
        {
            var rows = new List<string>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                    rows.Add(StableIdentity(collider) + "|" + StableSerializedState(collider) +
                        "|" + MatrixIdentity(collider.transform.localToWorldMatrix));
                foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                    rows.Add(StableIdentity(terrain) + "|" + AssetIdentity(terrain.terrainData) +
                        "|" + MatrixIdentity(terrain.transform.localToWorldMatrix));
            }
            rows.Sort(StringComparer.Ordinal);
            return Fingerprint(string.Join("\n", rows));
        }

        private static string StableSerializedState(UnityEngine.Object value)
        {
            // Editor JSON contains process-local instance IDs. Normalize every
            // object reference to its saved scene or asset identity before
            // hashing, so a clean disk reload can be compared faithfully.
            var references = new Dictionary<int, string>();
            references[0] = "null";
            var serialized = new SerializedObject(value);
            var property = serialized.GetIterator();
            while (property.Next(true))
                if (property.propertyType == SerializedPropertyType.ObjectReference)
                {
                    UnityEngine.Object reference = property.objectReferenceValue;
                    references[property.objectReferenceInstanceIDValue] = StableIdentity(reference);
                }
            return Regex.Replace(EditorJsonUtility.ToJson(value), "\"instanceID\"\\s*:\\s*(-?\\d+)", match =>
            {
                int id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                Require(references.TryGetValue(id, out string identity), "unmapped-serialized-native-reference");
                return "\"savedObjectIdentity\":\"" + identity + "\"";
            });
        }
    }
}
#endif
