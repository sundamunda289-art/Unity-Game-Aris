#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview.Editor
{
    /// <summary>Authors one shared native PVS for the saved bootstrap and all
    /// native cells. Streamed LODs and fallback silhouettes are occludees only;
    /// only persistent opaque immutable geometry may hide other renderers.</summary>
    public static class AarisUltraOcclusionBake
    {
        private const string Main = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const string ChunkDirectory = "Assets/AarisWorld/Scenes/Chunks";
        private const string Output = "/tmp/aaris-ultra-occlusion-bake";
        private const double BakeTimeoutSeconds = 900;
        private const double CancelTimeoutSeconds = 45;
        private const double ReloadTimeoutSeconds = 120;
        private const StaticEditorFlags OcclusionFlags =
            StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic;
        private static Session running;

        [Serializable] private sealed class Candidate
        {
            public string scene, hierarchy, kind, mesh, shaders;
        }
        [Serializable] private sealed class Audit
        {
            public string status, unityVersion;
            public string terrainInputPolicy = "Only the unchanged heightfield/holes are baked. Transient TerrainData clones have no tree instances, tree prototypes or detail prototypes; original data references are restored before any scene save.";
            public string[] scenes;
            public int terrainOccluders, opaqueMeshOccluders, staticOccludees, staticLodOccludees;
            public int excludedFallback, excludedLod, excludedMovingOrUnmarked, excludedMaterial;
            public long existingPvsBytes;
            public Candidate[] occluders;
        }
        [Serializable] private sealed class Backup
        {
            public string path, copy, sha256;
            public long lastWriteUtcTicks;
            public bool mayChange;
        }
        [Serializable] private sealed class BackupManifest
        {
            public string project, outputDirectory;
            public string[] originalFiles, originalDirectories;
            public Backup[] backups;
        }
        [Serializable] private sealed class Result
        {
            public string status, reason, backupDirectory, sharedPvsGuid, sharedPvsPath;
            public long pvsBytes, preSaveReportedPvsBytes, immediateReloadPvsBytes;
            public int reloadUpdateCallbacks;
            public bool reloadRenderAttempted;
            public double reloadWallSeconds;
            public int scenesVerified, terrainOccluders, opaqueMeshOccluders, staticOccludees;
            public bool sourceArchivesAndTerrainUnchanged, reloadedSharedPvs,
                freshPvsOutput, rolledBack, rollbackRequiresStoppedEditor;
            public int nativeErrors;
            public string[] changedPaths;
            public bool terrainDataRestoredBeforeSave;
            public TerrainInputEvidence[] terrainInputs;
        }
        [Serializable] private sealed class TerrainColliderEvidence
        {
            public string identity, dataIdentity;
        }
        [Serializable] private sealed class TerrainInputEvidence
        {
            public string identity, scene, hierarchy, sourceDataPath, sourceDataIdentity,
                sourceAssetSha256, heightAndHolesSha256;
            public int heightmapResolution, holesResolution, originalTreeInstances,
                originalTreePrototypes, originalDetailPrototypes, bakeTreeInstances,
                bakeTreePrototypes, bakeDetailPrototypes;
            public Vector3 size;
            public bool cloneVerified, restored, reloadedOriginalReference;
            public TerrainColliderEvidence[] colliders;
        }
        private sealed class TerrainBakeState
        {
            internal Terrain terrain;
            internal TerrainData original, clone;
            internal TerrainCollider[] colliders;
            internal TerrainData[] colliderData;
            internal TerrainInputEvidence evidence;
        }
        private sealed class FlagState
        {
            internal GameObject owner;
            internal string identity;
            internal StaticEditorFlags original, desired;
        }
        private sealed class CameraState
        {
            internal Camera camera;
            internal bool original;
        }
        private sealed class Session
        {
            internal string[] paths;
            internal readonly List<Scene> scenes = new List<Scene>();
            internal readonly List<FlagState> flags = new List<FlagState>();
            internal readonly List<CameraState> cameras = new List<CameraState>();
            internal readonly List<TerrainBakeState> terrainInputs = new List<TerrainBakeState>();
            internal readonly Dictionary<GameObject, StaticEditorFlags> desired =
                new Dictionary<GameObject, StaticEditorFlags>();
            internal readonly HashSet<Renderer> lodMembers = new HashSet<Renderer>();
            internal readonly List<Backup> backups = new List<Backup>();
            internal readonly HashSet<string> originalFiles = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> originalDirectories = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> expectedOutputDirectories = new HashSet<string>(StringComparer.Ordinal);
            internal string backupDirectory, failure, generatedDataPath, sharedPvsGuid;
            internal string firstNativeError;
            internal int nativeErrors;
            internal long preSaveReportedPvsBytes, immediateReloadPvsBytes;
            internal Audit audit;
            internal double deadline, cancelDeadline, firstCompletionAllowed, nextProgress, reloadDeadline, reloadStarted;
            internal bool cancelling, awaitingReload, reloadRenderAttempted, terrainDataRestoredBeforeSave;
            internal int reloadUpdateCallbacks;
        }

        private static void Require(bool value, string reason)
        { if (!value) throw new InvalidOperationException("AARIS_OCCLUSION_UNSAFE " + reason); }

        private static void RequireCleanEditor()
        {
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "play-mode-active");
            Require(running == null && !StaticOcclusionCulling.isRunning, "another-bake-active");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                Require(!SceneManager.GetSceneAt(i).isDirty, "unsaved-scene-work");
        }

        [MenuItem("Aaris/World/Probe shared native occlusion")]
        public static void Probe()
        {
            RequireCleanEditor();
            Session session = Inspect();
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Output, "probe.json"), JsonUtility.ToJson(session.audit, true));
            foreach (Scene scene in session.scenes)
                Require(!scene.isDirty, "probe-dirtied-scene");
            Debug.Log("AARIS_OCCLUSION_PROBE_PASS scenes=" + session.paths.Length +
                " terrain_occluders=" + session.audit.terrainOccluders +
                " persistent_mesh_occluders=" + session.audit.opaqueMeshOccluders +
                " static_occludees=" + session.audit.staticOccludees +
                " lod_occludees=" + session.audit.staticLodOccludees);
        }

        [MenuItem("Aaris/World/Bake shared native occlusion")]
        public static void Apply()
        {
            RequireCleanEditor();
            Require(Application.isBatchMode, "apply-requires-dedicated-batch");
            foreach (string argument in Environment.GetCommandLineArgs())
                Require(!string.Equals(argument, "-quit", StringComparison.OrdinalIgnoreCase),
                    "async-bake-must-launch-without-quit");
            Session session = Inspect();
            for (int i = 1; i < session.paths.Length; i++)
                Require(session.paths[i].StartsWith(ChunkDirectory + "/Aaris_Cell_", StringComparison.Ordinal),
                    "source-archives-cannot-be-rebaked");
            Require(session.audit.terrainOccluders + session.audit.opaqueMeshOccluders > 0 &&
                session.audit.staticOccludees > 0, "no-safe-occlusion-work");
            CaptureBackup(session);
            running = session;
            try
            {
                foreach (FlagState flag in session.flags)
                {
                    if (flag.original == flag.desired) continue;
                    GameObjectUtility.SetStaticEditorFlags(flag.owner, flag.desired);
                    if (PrefabUtility.IsPartOfPrefabInstance(flag.owner))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(flag.owner);
                }
                foreach (CameraState state in session.cameras)
                {
                    state.camera.useOcclusionCulling = true;
                    if (PrefabUtility.IsPartOfPrefabInstance(state.camera))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(state.camera);
                }
                SceneManager.SetActiveScene(session.scenes[0]);
                session.deadline = EditorApplication.timeSinceStartup + BakeTimeoutSeconds;
                session.firstCompletionAllowed = EditorApplication.timeSinceStartup + 1;
                session.nextProgress = EditorApplication.timeSinceStartup + 15;
                EditorApplication.update += PollBake;
                Application.logMessageReceivedThreaded += CaptureNativeError;
                PrepareHeightfieldOnlyTerrainInputs(session);
                Require(Volatile.Read(ref session.nativeErrors) == 0,
                    "terrain-input-editor-error " + session.firstNativeError);
                session.deadline = EditorApplication.timeSinceStartup + BakeTimeoutSeconds;
                session.firstCompletionAllowed = EditorApplication.timeSinceStartup + 1;
                Require(StaticOcclusionCulling.GenerateInBackground(), "native-bake-did-not-start");
                Debug.Log("AARIS_OCCLUSION_BAKE_STARTED scenes=" + session.paths.Length +
                    " timeout_seconds=" + BakeTimeoutSeconds + " backup=" + session.backupDirectory);
            }
            catch (Exception error) { RequestFailure(session, error.ToString()); }
        }

        private static Session Inspect()
        {
            string[] paths = AarisExplorerBuild.VerifiedBuildScenes();
            Require(paths.Length > 1 && paths[0] == Main, "unexpected-bootstrap-manifest");
            var session = new Session { paths = paths };
            session.scenes.Add(EditorSceneManager.OpenScene(Main, OpenSceneMode.Single));
            for (int i = 1; i < paths.Length; i++)
                session.scenes.Add(EditorSceneManager.OpenScene(paths[i], OpenSceneMode.Additive));
            SceneManager.SetActiveScene(session.scenes[0]);
            var audit = new Audit { status = "read-only-probe", unityVersion = Application.unityVersion,
                scenes = paths, existingPvsBytes = StaticOcclusionCulling.umbraDataSize };
            var candidates = new List<Candidate>();
            foreach (Scene scene in session.scenes)
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
                        foreach (LOD level in group.GetLODs())
                            foreach (Renderer renderer in level.renderers)
                                if (renderer != null) session.lodMembers.Add(renderer);
            foreach (Scene scene in session.scenes)
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                    {
                        if (scene.path != Main || !terrain.isActiveAndEnabled || terrain.terrainData == null ||
                            HasMovingAuthority(terrain.transform, false)) continue;
                        AddDesired(session, terrain.gameObject, StaticEditorFlags.OccluderStatic);
                        audit.terrainOccluders++;
                        candidates.Add(new Candidate { scene = scene.path, hierarchy = Hierarchy(terrain.transform),
                            kind = "persistent-terrain", mesh = AssetDatabase.GetAssetPath(terrain.terrainData),
                            shaders = terrain.materialTemplate != null && terrain.materialTemplate.shader != null
                                ? terrain.materialTemplate.shader.name : "native-terrain" });
                    }
                    foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        bool fallback = FallbackRelated(renderer);
                        bool lod = session.lodMembers.Contains(renderer) ||
                            renderer.GetComponentInParent<LODGroup>(true) != null ||
                            renderer.GetComponentInChildren<LODGroup>(true) != null;
                        if (!AuthoredStatic(renderer) || HasMovingAuthority(renderer.transform, true))
                        { audit.excludedMovingOrUnmarked++; continue; }
                        AddDesired(session, renderer.gameObject, StaticEditorFlags.OccludeeStatic);
                        audit.staticOccludees++;
                        if (lod) audit.staticLodOccludees++;
                        // All additive geometry can disappear when its scene
                        // retires. Its persistent alternative may have a
                        // different silhouette, so neither is an occluder.
                        if (fallback) { audit.excludedFallback++; continue; }
                        if (lod || scene.path != Main) { audit.excludedLod++; continue; }
                        if (!renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                            HasMovingAuthority(renderer.transform, false) || RiskyName(renderer.transform))
                        { audit.excludedMovingOrUnmarked++; continue; }
                        var filter = renderer.GetComponent<MeshFilter>();
                        if (filter == null || filter.sharedMesh == null || renderer.HasPropertyBlock() ||
                            !OpaqueStableMaterials(renderer, out string shaderNames))
                        { audit.excludedMaterial++; continue; }
                        AddDesired(session, renderer.gameObject, StaticEditorFlags.OccluderStatic);
                        audit.opaqueMeshOccluders++;
                        candidates.Add(new Candidate { scene = scene.path, hierarchy = Hierarchy(renderer.transform),
                            kind = "persistent-opaque-mesh", mesh = AssetDatabase.GetAssetPath(filter.sharedMesh),
                            shaders = shaderNames });
                    }
                    foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
                        session.cameras.Add(new CameraState { camera = camera, original = camera.useOcclusionCulling });
                    foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                    {
                        StaticEditorFlags original = GameObjectUtility.GetStaticEditorFlags(child.gameObject);
                        session.desired.TryGetValue(child.gameObject, out StaticEditorFlags selected);
                        session.flags.Add(new FlagState { owner = child.gameObject, original = original,
                            identity = GlobalObjectId.GetGlobalObjectIdSlow(child.gameObject).ToString(),
                            desired = (original & ~OcclusionFlags) | selected });
                    }
                }
            Require(session.cameras.Count > 0, "saved-gameplay-camera-missing");
            audit.occluders = candidates.ToArray();
            session.audit = audit;
            return session;
        }

        private static void AddDesired(Session session, GameObject owner, StaticEditorFlags add)
        {
            session.desired.TryGetValue(owner, out StaticEditorFlags existing);
            session.desired[owner] = existing | add;
        }

        private static bool AuthoredStatic(MeshRenderer renderer)
        {
            // Include the existing authored navigation/batching flags, but
            // never infer immobility solely from a mesh being visible today.
            const int authoredMask = 2 | 4 | 8 | 16;
            if (((int)GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & authoredMask) != 0) return true;
            LODGroup lod = renderer.GetComponentInParent<LODGroup>(true);
            return lod != null && ((int)GameObjectUtility.GetStaticEditorFlags(lod.gameObject) & authoredMask) != 0;
        }

        private static bool HasMovingAuthority(Transform node, bool allowFallback)
        {
            for (Transform parent = node; parent != null; parent = parent.parent)
            {
                if (parent.GetComponent<Rigidbody>() != null || parent.GetComponent<Animator>() != null ||
                    parent.GetComponent<CharacterController>() != null || parent.GetComponent<Cloth>() != null)
                    return true;
                foreach (MonoBehaviour script in parent.GetComponents<MonoBehaviour>())
                    if (script == null || !(allowFallback && script is AarisChunkFallbackVisibility)) return true;
            }
            foreach (Component component in node.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Rigidbody || component is Animator ||
                    component is CharacterController || component is Cloth) return true;
                if (component is MonoBehaviour && !(allowFallback && component is AarisChunkFallbackVisibility))
                    return true;
            }
            return false;
        }

        private static bool FallbackRelated(MeshRenderer renderer) =>
            renderer.GetComponentInParent<AarisChunkFallbackVisibility>(true) != null ||
            renderer.GetComponentInChildren<AarisChunkFallbackVisibility>(true) != null;

        private static readonly string[] RiskyTerms =
            { "glass", "window", "door", "water", "foliage", "tree", "leaf", "fence", "rail", "grate", "gate" };
        private static bool RiskyName(Transform node)
        {
            for (Transform parent = node; parent != null; parent = parent.parent)
                foreach (string term in RiskyTerms)
                    if (parent.name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool OpaqueStableMaterials(MeshRenderer renderer, out string shaderNames)
        {
            shaderNames = "";
            Material[] materials = renderer.sharedMaterials;
            if (materials.Length == 0) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Material material in materials)
            {
                if (material == null || material.shader == null || !material.shader.isSupported) return false;
                string shader = material.shader.name;
                if (shader != "Universal Render Pipeline/Lit" && shader != "Universal Render Pipeline/Simple Lit" &&
                    shader != "Universal Render Pipeline/Unlit" && shader != "Universal Render Pipeline/Baked Lit") return false;
                if (material.renderQueue >= (int)RenderQueue.AlphaTest ||
                    material.GetTag("RenderType", true, "Opaque") != "Opaque" ||
                    (material.HasProperty("_Surface") && material.GetFloat("_Surface") > .001f) ||
                    (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > .001f) ||
                    material.IsKeywordEnabled("_ALPHATEST_ON") || material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT") ||
                    material.IsKeywordEnabled("_ALPHABLEND_ON") || material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON")) return false;
                names.Add(shader);
            }
            shaderNames = string.Join(";", names);
            return true;
        }

        private static string Hierarchy(Transform node)
        {
            string result = node.name;
            for (Transform parent = node.parent; parent != null; parent = parent.parent)
                result = parent.name + "/" + result;
            return result;
        }

        private static string Slash(string path) => path.Replace('\\', '/');
        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static string ObjectIdentity(UnityEngine.Object value) => value == null
            ? "null" : GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();

        private static string HashTerrainShape(TerrainData data)
        {
            // Full float/boolean samples, not an approximate visual or bounds
            // comparison. This runs only in the offline authoring utility.
            int heightResolution = data.heightmapResolution;
            int holesResolution = data.holesResolution;
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes))
            using (var sha = SHA256.Create())
            {
                writer.Write(heightResolution); writer.Write(holesResolution);
                Vector3 size = data.size;
                writer.Write(size.x); writer.Write(size.y); writer.Write(size.z);
                foreach (float height in data.GetHeights(0, 0, heightResolution, heightResolution))
                    writer.Write(height);
                if (holesResolution > 0)
                    foreach (bool solid in data.GetHoles(0, 0, holesResolution, holesResolution))
                        writer.Write(solid);
                writer.Flush(); bytes.Position = 0;
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void PrepareHeightfieldOnlyTerrainInputs(Session session)
        {
            Require(!StaticOcclusionCulling.isRunning && session.terrainInputs.Count == 0,
                "terrain-input-preparation-state");
            foreach (Scene scene in session.scenes)
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                    {
                        if (!session.desired.TryGetValue(terrain.gameObject, out StaticEditorFlags flags) ||
                            (flags & StaticEditorFlags.OccluderStatic) == 0) continue;
                        TerrainData source = terrain.terrainData;
                        string path = AssetDatabase.GetAssetPath(source);
                        Require(source != null && !string.IsNullOrEmpty(path) && File.Exists(path),
                            "terrain-isolation-source-not-persistent");
                        var evidence = new TerrainInputEvidence {
                            identity = ObjectIdentity(terrain), scene = scene.path,
                            hierarchy = Hierarchy(terrain.transform), sourceDataPath = path,
                            sourceDataIdentity = ObjectIdentity(source), sourceAssetSha256 = HashFile(path),
                            heightAndHolesSha256 = HashTerrainShape(source), size = source.size,
                            heightmapResolution = source.heightmapResolution, holesResolution = source.holesResolution,
                            originalTreeInstances = source.treeInstanceCount,
                            originalTreePrototypes = source.treePrototypes.Length,
                            originalDetailPrototypes = source.detailPrototypes.Length };
                        var state = new TerrainBakeState { terrain = terrain, original = source,
                            colliders = terrain.GetComponents<TerrainCollider>(), evidence = evidence };
                        state.colliderData = new TerrainData[state.colliders.Length];
                        evidence.colliders = new TerrainColliderEvidence[state.colliders.Length];
                        for (int i = 0; i < state.colliders.Length; i++)
                        {
                            state.colliderData[i] = state.colliders[i].terrainData;
                            evidence.colliders[i] = new TerrainColliderEvidence {
                                identity = ObjectIdentity(state.colliders[i]),
                                dataIdentity = ObjectIdentity(state.colliderData[i]) };
                        }
                        // Register ownership BEFORE allocating/changing anything,
                        // so every partial failure can restore previous inputs.
                        session.terrainInputs.Add(state);
                        state.clone = UnityEngine.Object.Instantiate(source);
                        Require(state.clone != null && state.clone != source &&
                            !AssetDatabase.Contains(state.clone), "terrain-clone-not-private");
                        state.clone.name = "__AarisOcclusionHeightfieldOnly_" + terrain.GetInstanceID();
                        state.clone.hideFlags = HideFlags.DontSave;
                        state.clone.SetTreeInstances(Array.Empty<TreeInstance>(), false);
                        state.clone.treePrototypes = Array.Empty<TreePrototype>();
                        state.clone.detailPrototypes = Array.Empty<DetailPrototype>();
                        Require(HashTerrainShape(state.clone) == evidence.heightAndHolesSha256,
                            "terrain-clone-height-or-holes-drift");
                        terrain.terrainData = state.clone;
                        terrain.Flush();
                        VerifyTerrainInputIsolated(state);
                        evidence.bakeTreeInstances = state.clone.treeInstanceCount;
                        evidence.bakeTreePrototypes = state.clone.treePrototypes.Length;
                        evidence.bakeDetailPrototypes = state.clone.detailPrototypes.Length;
                        evidence.cloneVerified = true;
                    }
            Require(session.terrainInputs.Count == session.audit.terrainOccluders,
                "terrain-isolation-candidate-count-mismatch");
            Debug.Log("AARIS_OCCLUSION_HEIGHTFIELD_INPUTS terrains=" + session.terrainInputs.Count +
                " tree_instances=0 tree_prototypes=0 detail_prototypes=0");
        }

        private static void VerifyTerrainInputIsolated(TerrainBakeState state)
        {
            Require(state.terrain != null && state.clone != null && state.terrain.terrainData == state.clone &&
                !AssetDatabase.Contains(state.clone), "terrain-clone-reference-drift");
            Require(state.clone.treeInstanceCount == 0 && state.clone.treePrototypes.Length == 0 &&
                state.clone.detailPrototypes.Length == 0, "terrain-foliage-remains-in-bake-input");
            Require(HashTerrainShape(state.clone) == state.evidence.heightAndHolesSha256,
                "terrain-bake-input-shape-drift");
            VerifyOriginalTerrainData(state);
            for (int i = 0; i < state.colliders.Length; i++)
                Require(state.colliders[i] != null && state.colliders[i].terrainData == state.colliderData[i],
                    "terrain-collider-reference-changed-during-bake");
        }

        private static void VerifyOriginalTerrainData(TerrainBakeState state)
        {
            TerrainInputEvidence e = state.evidence;
            Require(state.original != null && ObjectIdentity(state.original) == e.sourceDataIdentity &&
                AssetDatabase.GetAssetPath(state.original) == e.sourceDataPath &&
                HashFile(e.sourceDataPath) == e.sourceAssetSha256 &&
                HashTerrainShape(state.original) == e.heightAndHolesSha256 &&
                state.original.treeInstanceCount == e.originalTreeInstances &&
                state.original.treePrototypes.Length == e.originalTreePrototypes &&
                state.original.detailPrototypes.Length == e.originalDetailPrototypes,
                "original-terrain-data-changed-during-isolation");
        }

        private static void RestoreTerrainInputs(Session session, bool verifyBakedInputs)
        {
            Require(!StaticOcclusionCulling.isRunning, "cannot-restore-terrain-while-native-bake-runs");
            var failures = new List<string>();
            // Try every restore even if one invariant fails. Never save a scene
            // with a transient data reference, and never destroy an in-use input.
            foreach (TerrainBakeState state in session.terrainInputs)
            {
                // Saved scenes can already have been reopened before a later
                // verification fails. Their old component handles are then
                // destroyed, but all transient inputs were restored/destroyed
                // before that reopen. Do not block file rollback on those handles.
                if (!verifyBakedInputs && state.clone == null && state.evidence.restored) continue;
                if (verifyBakedInputs)
                    try { VerifyTerrainInputIsolated(state); }
                    catch (Exception error) { failures.Add(error.ToString()); }
                try
                {
                    Require(state.terrain != null && state.original != null, "terrain-restore-owner-missing");
                    state.terrain.terrainData = state.original;
                    state.terrain.Flush();
                    Require(state.terrain.terrainData == state.original, "terrain-reference-not-restored");
                    for (int i = 0; i < state.colliders.Length; i++)
                    {
                        // On failure, repair an unexpected native side effect
                        // before reopening the byte-identical saved scene.
                        if (!verifyBakedInputs && state.colliders[i] != null &&
                            state.colliders[i].terrainData != state.colliderData[i])
                            state.colliders[i].terrainData = state.colliderData[i];
                        Require(state.colliders[i] != null && state.colliders[i].terrainData == state.colliderData[i],
                            "terrain-collider-reference-not-preserved");
                    }
                    if (verifyBakedInputs) VerifyOriginalTerrainData(state);
                    state.evidence.restored = true;
                }
                catch (Exception error) { failures.Add(error.ToString()); }
                if (state.clone != null && state.terrain != null && state.terrain.terrainData != state.clone)
                    try
                    {
                        bool referenced = false;
                        foreach (TerrainCollider collider in state.colliders)
                            if (collider != null && collider.terrainData == state.clone) referenced = true;
                        Require(!referenced, "cannot-destroy-terrain-input-still-used-by-collider");
                        UnityEngine.Object.DestroyImmediate(state.clone);
                        state.clone = null;
                    }
                    catch (Exception error) { failures.Add(error.ToString()); }
            }
            Require(failures.Count == 0, "terrain-input-restoration-failed " + string.Join("\n", failures));
            if (verifyBakedInputs)
            {
                Require(session.terrainInputs.Count == session.audit.terrainOccluders,
                    "restored-terrain-input-count-mismatch");
                session.terrainDataRestoredBeforeSave = true;
            }
        }

        private static void VerifyReloadedTerrainReferences(Session session)
        {
            Require(session.terrainDataRestoredBeforeSave, "terrain-not-restored-before-scene-save");
            var expected = new Dictionary<string, TerrainInputEvidence>(StringComparer.Ordinal);
            foreach (TerrainBakeState state in session.terrainInputs) expected.Add(state.evidence.identity, state.evidence);
            int count = 0;
            foreach (Scene scene in session.scenes)
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                    {
                        if (!expected.TryGetValue(ObjectIdentity(terrain), out TerrainInputEvidence e)) continue;
                        TerrainData data = terrain.terrainData;
                        Require(data != null && ObjectIdentity(data) == e.sourceDataIdentity &&
                            AssetDatabase.GetAssetPath(data) == e.sourceDataPath &&
                            HashFile(e.sourceDataPath) == e.sourceAssetSha256 &&
                            HashTerrainShape(data) == e.heightAndHolesSha256 &&
                            data.treeInstanceCount == e.originalTreeInstances &&
                            data.treePrototypes.Length == e.originalTreePrototypes &&
                            data.detailPrototypes.Length == e.originalDetailPrototypes,
                            "reloaded-terrain-original-data-mismatch");
                        TerrainCollider[] colliders = terrain.GetComponents<TerrainCollider>();
                        Require(colliders.Length == e.colliders.Length, "reloaded-terrain-collider-count-drift");
                        for (int i = 0; i < colliders.Length; i++)
                            Require(ObjectIdentity(colliders[i]) == e.colliders[i].identity &&
                                ObjectIdentity(colliders[i].terrainData) == e.colliders[i].dataIdentity,
                                "reloaded-terrain-collider-reference-drift");
                        Require(e.cloneVerified && e.restored, "terrain-isolation-proof-incomplete");
                        e.reloadedOriginalReference = true;
                        count++;
                    }
            Require(count == expected.Count, "reloaded-terrain-owner-count-drift");
        }

        private static void DestroyDetachedTerrainInputs(Session session)
        {
            Require(!StaticOcclusionCulling.isRunning, "native-bake-still-uses-terrain-inputs");
            foreach (TerrainBakeState state in session.terrainInputs)
            {
                if (state.clone == null) continue;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    foreach (GameObject root in SceneManager.GetSceneAt(i).GetRootGameObjects())
                    {
                        foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                            Require(terrain.terrainData != state.clone, "rollback-terrain-still-uses-clone");
                        foreach (TerrainCollider collider in root.GetComponentsInChildren<TerrainCollider>(true))
                            Require(collider.terrainData != state.clone, "rollback-collider-still-uses-clone");
                    }
                UnityEngine.Object.DestroyImmediate(state.clone);
                state.clone = null;
            }
        }

        private static void CaptureBackup(Session session)
        {
            session.backupDirectory = "/tmp/aaris-ultra-occlusion-backup-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(session.backupDirectory);
            foreach (string path in Directory.GetFiles("Assets", "*", SearchOption.AllDirectories))
                session.originalFiles.Add(Slash(path));
            foreach (string path in Directory.GetDirectories("Assets", "*", SearchOption.AllDirectories))
                session.originalDirectories.Add(Slash(path));
            var files = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (string path in session.paths)
            { files[path] = true; files[path + ".meta"] = false; }
            files["ProjectSettings/EditorBuildSettings.asset"] = false;
            if (File.Exists("ProjectSettings/EditorSettings.asset"))
                files["ProjectSettings/EditorSettings.asset"] = false;
            foreach (string path in Directory.GetFiles(ChunkDirectory, "*_Detail.unity"))
            {
                string source = Slash(path);
                if (Path.GetFileName(source).StartsWith("Aaris_Cell_", StringComparison.Ordinal)) continue;
                files[source] = false; files[source + ".meta"] = false;
            }
            foreach (Scene scene in session.scenes)
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                    {
                        string path = AssetDatabase.GetAssetPath(terrain.terrainData);
                        Require(!string.IsNullOrEmpty(path) && File.Exists(path), "terrain-data-not-persistent");
                        files[path] = false; files[path + ".meta"] = false;
                    }
            // Unity stores the PVS beside its active scene. Include the
            // documented Assets/<scene-name> location as a conservative guard.
            session.expectedOutputDirectories.Add(Slash(Path.ChangeExtension(Main, null)));
            session.expectedOutputDirectories.Add("Assets/" + Path.GetFileNameWithoutExtension(Main));
            foreach (string directory in session.expectedOutputDirectories)
            {
                if (File.Exists(directory + ".meta")) files[directory + ".meta"] = false;
                if (!Directory.Exists(directory)) continue;
                foreach (string path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                    files[Slash(path)] = Path.GetFileName(path) == "OcclusionCullingData.asset";
            }
            foreach (string path in Directory.GetFiles("Assets", "OcclusionCullingData.asset", SearchOption.AllDirectories))
            {
                string normalized = Slash(path);
                files[normalized] = true;
                if (File.Exists(normalized + ".meta")) files[normalized + ".meta"] = false;
            }
            int ordinal = 0;
            foreach (var file in files)
            {
                Require(File.Exists(file.Key), "backup-source-missing-" + file.Key);
                string copy = Path.Combine(session.backupDirectory, (ordinal++).ToString("D5") + ".bytes");
                File.Copy(file.Key, copy);
                session.backups.Add(new Backup { path = file.Key, copy = copy,
                    sha256 = HashFile(file.Key), lastWriteUtcTicks = File.GetLastWriteTimeUtc(file.Key).Ticks,
                    mayChange = file.Value });
            }
            var manifest = new BackupManifest { project = Directory.GetCurrentDirectory(),
                outputDirectory = Output, backups = session.backups.ToArray(),
                originalFiles = new List<string>(session.originalFiles).ToArray(),
                originalDirectories = new List<string>(session.originalDirectories).ToArray() };
            File.WriteAllText(Path.Combine(session.backupDirectory, "restore-manifest.json"), JsonUtility.ToJson(manifest, true));
        }

        private static void PollBake()
        {
            Session session = running;
            if (session == null) { EditorApplication.update -= PollBake; return; }
            try
            {
                if (session.cancelling)
                {
                    if (!StaticOcclusionCulling.isRunning) { FinishFailure(session); return; }
                    if (EditorApplication.timeSinceStartup > session.cancelDeadline)
                    {
                        // Never copy backups while a native writer is still
                        // running. Parent process can restore the exact manifest
                        // after this dedicated Editor has fully stopped.
                        WriteResult(session, new Result { status = "failed", reason = session.failure,
                            backupDirectory = session.backupDirectory, nativeErrors = session.nativeErrors,
                            rollbackRequiresStoppedEditor = true });
                        EditorApplication.update -= PollBake;
                        Application.logMessageReceivedThreaded -= CaptureNativeError;
                        running = null;
                        EditorApplication.Exit(5);
                    }
                    return;
                }
                if (Volatile.Read(ref session.nativeErrors) != 0)
                {
                    RequestFailure(session, "native-editor-error " + session.firstNativeError);
                    return;
                }
                if (session.awaitingReload)
                {
                    PollSavedReloadVerification(session);
                    return;
                }
                if (StaticOcclusionCulling.isRunning)
                {
                    if (EditorApplication.timeSinceStartup >= session.nextProgress)
                    {
                        session.nextProgress = EditorApplication.timeSinceStartup + 15;
                        Debug.Log("AARIS_OCCLUSION_BAKE_RUNNING remaining_seconds=" +
                            Math.Max(0, session.deadline - EditorApplication.timeSinceStartup).ToString("F0"));
                    }
                    if (EditorApplication.timeSinceStartup > session.deadline)
                        RequestFailure(session, "native-bake-timeout");
                    return;
                }
                if (EditorApplication.timeSinceStartup < session.firstCompletionAllowed) return;
                BeginSavedReloadVerification(session);
            }
            catch (Exception error) { RequestFailure(session, error.ToString()); }
        }

        private static void RequestFailure(Session session, string reason)
        {
            if (session.cancelling) return;
            session.failure = reason;
            session.cancelling = true;
            session.cancelDeadline = EditorApplication.timeSinceStartup + CancelTimeoutSeconds;
            if (StaticOcclusionCulling.isRunning) StaticOcclusionCulling.Cancel();
            if (!StaticOcclusionCulling.isRunning) FinishFailure(session);
        }

        private static void CaptureNativeError(string condition, string stackTrace, LogType type)
        {
            Session session = running;
            if (session == null || session.cancelling ||
                (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            Interlocked.CompareExchange(ref session.firstNativeError, condition + "\n" + stackTrace, null);
            Interlocked.Increment(ref session.nativeErrors);
        }

        private static HashSet<string> FreshPvsOutputs(Session session)
        {
            // A failed rebake can leave an older positive PVS loaded. Require
            // newly written data, then match this evidence to the exact shared
            // asset referenced by every saved scene before declaring success.
            var fresh = new HashSet<string>(StringComparer.Ordinal);
            foreach (string source in Directory.GetFiles("Assets", "OcclusionCullingData.asset", SearchOption.AllDirectories))
            {
                string path = Slash(source);
                if (new FileInfo(path).Length == 0) continue;
                if (!session.originalFiles.Contains(path))
                {
                    if (OwnedBakeOutput(session, path)) fresh.Add(path);
                    continue;
                }
                foreach (Backup backup in session.backups)
                    if (backup.path == path && backup.mayChange &&
                        (File.GetLastWriteTimeUtc(path).Ticks != backup.lastWriteUtcTicks ||
                            HashFile(path) != backup.sha256))
                    { fresh.Add(path); break; }
            }
            return fresh;
        }

        private static void BeginSavedReloadVerification(Session session)
        {
            Require(Volatile.Read(ref session.nativeErrors) == 0, "native-editor-error " + session.firstNativeError);
            RestoreTerrainInputs(session, true);
            Require(Volatile.Read(ref session.nativeErrors) == 0,
                "terrain-restore-editor-error " + session.firstNativeError);
            session.preSaveReportedPvsBytes = StaticOcclusionCulling.umbraDataSize;
            HashSet<string> freshOutputs = FreshPvsOutputs(session);
            Require(freshOutputs.Count != 0, "native-pvs-not-newly-written");
            foreach (Backup backup in session.backups)
                if (!backup.mayChange) Require(HashFile(backup.path) == backup.sha256,
                    "protected-source-changed-before-save-" + backup.path);
            // umbraDataSize describes the scene's saved PVS, so a first bake
            // can report zero while its newly written asset is already present
            // but the scene's old on-disk reference is still empty. Require
            // fresh owned output and zero native errors before this temporary
            // save; disk reload below must prove a positive shared native PVS.
            // Any reference/data failure still restores the exact backups.
            foreach (Scene scene in session.scenes)
                Require(EditorSceneManager.SaveScene(scene, scene.path), "native-pvs-scene-save-failed");
            AssetDatabase.Refresh();
            string guid = VerifySharedReferences(session.paths, out string dataPath);
            session.sharedPvsGuid = guid;
            session.generatedDataPath = dataPath;
            Require(freshOutputs.Contains(dataPath), "shared-pvs-is-stale-or-unowned");
            session.scenes.Clear();
            session.scenes.Add(EditorSceneManager.OpenScene(Main, OpenSceneMode.Single));
            for (int i = 1; i < session.paths.Length; i++)
                session.scenes.Add(EditorSceneManager.OpenScene(session.paths[i], OpenSceneMode.Additive));
            SceneManager.SetActiveScene(session.scenes[0]);
            session.immediateReloadPvsBytes = StaticOcclusionCulling.umbraDataSize;
            session.awaitingReload = true;
            session.reloadStarted = EditorApplication.timeSinceStartup;
            session.reloadDeadline = session.reloadStarted + ReloadTimeoutSeconds;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            Debug.Log("AARIS_OCCLUSION_RELOAD_WAIT immediate_pvs_bytes=" +
                session.immediateReloadPvsBytes + " pre_save_pvs_bytes=" + session.preSaveReportedPvsBytes);
        }

        private static void PollSavedReloadVerification(Session session)
        {
            session.reloadUpdateCallbacks++;
            Require(EditorApplication.timeSinceStartup <= session.reloadDeadline,
                "reloaded-native-pvs-publication-timeout");
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            if (session.reloadUpdateCallbacks < 2 || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            long bytes = StaticOcclusionCulling.umbraDataSize;
            if (bytes > 0)
            {
                Debug.Log("AARIS_OCCLUSION_RELOAD_PUBLISHED pvs_bytes=" + bytes +
                    " update_callbacks=" + session.reloadUpdateCallbacks +
                    " explicit_render=" + session.reloadRenderAttempted);
                FinishVerifiedSuccess(session);
                return;
            }
            if (!session.reloadRenderAttempted && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            {
                // Unity's counter reads GetRendererScene(), rather than the
                // imported asset directly. A batch Editor may not publish the
                // reopened scene to that native world until an actual render.
                // Use its authored camera/mask so the normal culling path runs;
                // do not create another scene or replace the positive-size gate.
                Camera camera = null;
                foreach (GameObject root in session.scenes[0].GetRootGameObjects())
                {
                    camera = root.GetComponentInChildren<Camera>(true);
                    if (camera != null) break;
                }
                Require(camera != null, "reloaded-main-camera-missing");
                RenderTexture originalTarget = camera.targetTexture;
                RenderTexture originalActive = RenderTexture.active;
                var target = new RenderTexture(16, 16, 16, RenderTextureFormat.ARGB32);
                session.reloadRenderAttempted = true;
                try
                {
                    Require(target.Create(), "reload-render-target-create-failed");
                    camera.targetTexture = target;
                    camera.Render();
                }
                finally
                {
                    camera.targetTexture = originalTarget;
                    RenderTexture.active = originalActive;
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
                Debug.Log("AARIS_OCCLUSION_RELOAD_RENDER pvs_bytes=" + StaticOcclusionCulling.umbraDataSize);
                // Always return to the Editor before testing final invariants.
            }
        }

        private static void FinishVerifiedSuccess(Session session)
        {
            string guid = session.sharedPvsGuid;
            string dataPath = session.generatedDataPath;
            Require(StaticOcclusionCulling.umbraDataSize > 0, "reloaded-native-pvs-empty");
            Require(VerifySharedReferences(session.paths, out _) == guid, "reloaded-pvs-reference-drift");
            VerifyReloadedFlags(session);
            VerifyReloadedTerrainReferences(session);
            Require(Volatile.Read(ref session.nativeErrors) == 0, "saved-reload-editor-error " + session.firstNativeError);
            var changed = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Backup backup in session.backups)
            {
                bool same = File.Exists(backup.path) && HashFile(backup.path) == backup.sha256;
                Require(backup.mayChange || same, "protected-source-changed-after-save-" + backup.path);
                if (!same) changed.Add(backup.path);
            }
            changed.Add(dataPath);
            if (!session.originalFiles.Contains(dataPath + ".meta")) changed.Add(dataPath + ".meta");
            for (string directory = Slash(Path.GetDirectoryName(dataPath));
                 !string.IsNullOrEmpty(directory) && directory != "Assets";
                 directory = Slash(Path.GetDirectoryName(directory)))
                if (!session.originalFiles.Contains(directory + ".meta") && File.Exists(directory + ".meta"))
                    changed.Add(directory + ".meta");
            var result = new Result { status = "passed", backupDirectory = session.backupDirectory,
                sharedPvsGuid = guid, sharedPvsPath = dataPath, pvsBytes = StaticOcclusionCulling.umbraDataSize,
                preSaveReportedPvsBytes = session.preSaveReportedPvsBytes,
                immediateReloadPvsBytes = session.immediateReloadPvsBytes,
                reloadUpdateCallbacks = session.reloadUpdateCallbacks,
                reloadRenderAttempted = session.reloadRenderAttempted,
                reloadWallSeconds = EditorApplication.timeSinceStartup - session.reloadStarted,
                scenesVerified = session.paths.Length, sourceArchivesAndTerrainUnchanged = true,
                reloadedSharedPvs = true, freshPvsOutput = true, nativeErrors = session.nativeErrors,
                terrainOccluders = session.audit.terrainOccluders,
                opaqueMeshOccluders = session.audit.opaqueMeshOccluders, staticOccludees = session.audit.staticOccludees,
                changedPaths = new List<string>(changed).ToArray() };
            WriteResult(session, result);
            File.WriteAllLines(Path.Combine(Output, "changed-paths.txt"), result.changedPaths);
            Debug.Log("AARIS_OCCLUSION_BAKE_RELOAD_PASS scenes=" + result.scenesVerified +
                " pvs_bytes=" + result.pvsBytes + " terrain_occluders=" + result.terrainOccluders +
                " persistent_mesh_occluders=" + result.opaqueMeshOccluders);
            EditorApplication.update -= PollBake;
            Application.logMessageReceivedThreaded -= CaptureNativeError;
            running = null;
            EditorApplication.Exit(0);
        }

        private static string VerifySharedReferences(string[] paths, out string dataPath)
        {
            string guid = null;
            foreach (string path in paths)
            {
                Match match = Regex.Match(File.ReadAllText(path),
                    @"(?m)^  m_OcclusionCullingData: \{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: \d+\}\s*$");
                Require(match.Success && match.Groups[1].Value != "0", "saved-shared-pvs-reference-missing-" + path);
                string found = match.Groups[2].Value;
                if (guid == null) guid = found;
                else Require(guid == found, "saved-additive-pvs-guid-mismatch");
            }
            dataPath = AssetDatabase.GUIDToAssetPath(guid);
            Require(!string.IsNullOrEmpty(dataPath) && File.Exists(dataPath) &&
                new FileInfo(dataPath).Length > 0, "saved-pvs-asset-missing");
            return guid;
        }

        private static void VerifyReloadedFlags(Session session)
        {
            var expected = new Dictionary<string, StaticEditorFlags>(StringComparer.Ordinal);
            foreach (FlagState flag in session.flags) expected.Add(flag.identity, flag.desired);
            int count = 0;
            foreach (Scene scene in session.scenes)
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        string identity = GlobalObjectId.GetGlobalObjectIdSlow(transform.gameObject).ToString();
                        bool found = expected.TryGetValue(identity, out StaticEditorFlags flags);
                        Require(found && GameObjectUtility.GetStaticEditorFlags(transform.gameObject) == flags,
                            "saved-static-flags-or-object-identity-drift");
                        count++;
                    }
                    foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
                        Require(camera.useOcclusionCulling, "saved-camera-occlusion-disabled");
                }
            Require(count == expected.Count, "saved-gameobject-count-drift");
        }

        private static void FinishFailure(Session session)
        {
            EditorApplication.update -= PollBake;
            Application.logMessageReceivedThreaded -= CaptureNativeError;
            var result = new Result { status = "failed", reason = session.failure,
                backupDirectory = session.backupDirectory, nativeErrors = session.nativeErrors,
                preSaveReportedPvsBytes = session.preSaveReportedPvsBytes,
                immediateReloadPvsBytes = session.immediateReloadPvsBytes,
                reloadUpdateCallbacks = session.reloadUpdateCallbacks,
                reloadRenderAttempted = session.reloadRenderAttempted,
                reloadWallSeconds = session.awaitingReload
                    ? EditorApplication.timeSinceStartup - session.reloadStarted : 0 };
            try
            {
                // File rollback must still run if a native object disappeared
                // or a data setter itself failed during in-memory cleanup.
                try { RestoreTerrainInputs(session, false); }
                catch (Exception error) { result.reason += "\nTERRAIN_RESTORE_RETRY " + error; }
                foreach (FlagState flag in session.flags)
                    if (flag.owner != null) GameObjectUtility.SetStaticEditorFlags(flag.owner, flag.original);
                foreach (CameraState state in session.cameras)
                    if (state.camera != null) state.camera.useOcclusionCulling = state.original;
                foreach (Backup backup in session.backups)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup.path));
                    File.Copy(backup.copy, backup.path, true);
                }
                foreach (string path in Directory.GetFiles("Assets", "OcclusionCullingData.asset", SearchOption.AllDirectories))
                {
                    string normalized = Slash(path);
                    if (session.originalFiles.Contains(normalized) || !OwnedBakeOutput(session, normalized)) continue;
                    File.Delete(normalized);
                    if (!session.originalFiles.Contains(normalized + ".meta") && File.Exists(normalized + ".meta"))
                        File.Delete(normalized + ".meta");
                    string directory = Slash(Path.GetDirectoryName(normalized));
                    if (!session.originalDirectories.Contains(directory) && Directory.Exists(directory) &&
                        Directory.GetFileSystemEntries(directory).Length == 0)
                    {
                        Directory.Delete(directory);
                        if (!session.originalFiles.Contains(directory + ".meta") && File.Exists(directory + ".meta"))
                            File.Delete(directory + ".meta");
                    }
                }
                AssetDatabase.Refresh();
                EditorSceneManager.OpenScene(Main, OpenSceneMode.Single);
                DestroyDetachedTerrainInputs(session);
                foreach (Backup backup in session.backups)
                    Require(HashFile(backup.path) == backup.sha256, "rollback-byte-hash-mismatch-" + backup.path);
                result.rolledBack = true;
            }
            catch (Exception error)
            {
                result.reason += "\nROLLBACK_FAILURE " + error;
                result.rollbackRequiresStoppedEditor = true;
            }
            WriteResult(session, result);
            Debug.LogError("AARIS_OCCLUSION_BAKE_FAILED rolled_back=" + result.rolledBack + " " + result.reason);
            running = null;
            EditorApplication.Exit(4);
        }

        private static void WriteResult(Session session, Result result)
        {
            result.terrainDataRestoredBeforeSave = session.terrainDataRestoredBeforeSave;
            result.terrainInputs = new TerrainInputEvidence[session.terrainInputs.Count];
            for (int i = 0; i < result.terrainInputs.Length; i++)
                result.terrainInputs[i] = session.terrainInputs[i].evidence;
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Output, "apply-result.json"), JsonUtility.ToJson(result, true));
            File.WriteAllText(Path.Combine(Output, "applied-candidates.json"), JsonUtility.ToJson(session.audit, true));
        }

        private static bool OwnedBakeOutput(Session session, string path)
        {
            if (path == session.generatedDataPath) return true;
            foreach (string directory in session.expectedOutputDirectories)
                if (path.StartsWith(directory + "/", StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
#endif
