#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Creates material-merged presentation meshes in the Editor. Source meshes,
    /// prefabs, physics, LOD membership and fallback ownership are never rewritten.
    /// Generated renderers remain off in the saved scene; the runtime cohort
    /// validates its sources before atomically replacing their presentation.
    /// </summary>
    public static class AarisUltraStaticBatchBake
    {
        private const string Main = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const string Generated = "Assets/AarisWorld/Generated/StaticBatches";
        private const string HostPrefix = "__AarisBakedStatic_";
        private const int VertexLimit = 60000;
        private const float CellSize = 400f;
        private static readonly string Output = "/tmp/aaris-ultra-static-bake";
        private static bool hasReflectionProbes;

        [Serializable] private sealed class Row
        {
            public string key, scene, fallbackScene, host;
            public int sources, inputSubmeshes, outputs, vertices;
            public long indices;
            public string[] sourceHierarchies, assets;
            public Matrix4x4[] sourceMatrices, outputMatrices;
        }
        [Serializable] private sealed class AssetProof
        {
            public string path, sha256;
            public long bytes;
        }
        [Serializable] private sealed class Report
        {
            public string status, unityVersion, reason;
            public int considered, eligible, excludedLod, excludedState, excludedMaterial,
                cohorts, sourceRenderers, sourceSubmeshes, combinedRenderers, vertices,
                authoredLightProbeCount, authoredReflectionProbeCount;
            public long indices;
            public bool protectedSourceBytesUnchanged, originalRendererStateUnchanged,
                sourcePhysicsUnchanged, savedOutputsDisabled, reloadedReferencesValid;
            public Row[] rows;
            public AssetProof[] generatedAssets, sourceAssets;
        }
        private sealed class Cohort
        {
            internal string key, fallback;
            internal readonly List<MeshRenderer> sources = new List<MeshRenderer>();
        }
        private sealed class Part
        {
            internal MeshRenderer renderer;
            internal Mesh mesh;
            internal int submesh;
        }
        private sealed class MaterialGroup
        {
            internal string key;
            internal MeshRenderer exemplar;
            internal Material material;
            internal readonly List<Part> parts = new List<Part>();
        }
        private sealed class BuiltMesh
        {
            internal Mesh mesh;
            internal MaterialGroup group;
        }
        private sealed class SourceState
        {
            internal MeshRenderer renderer;
            internal Mesh mesh;
            internal bool enabled, forceOff;
            internal Matrix4x4 matrix;
            internal Material[] materials;
        }

        [MenuItem("Aaris/World/Bake immutable presentation cohorts")]
        public static void Run()
        {
            Directory.CreateDirectory(Output);
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "requires-edit-mode");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                Require(!SceneManager.GetSceneAt(i).isDirty, "unsaved-scene");
            Require(!Directory.Exists(Generated), "generated-directory-already-exists-refuse-overwrite");
            var report = new Report { status = "running", unityVersion = Application.unityVersion };
            var generated = new List<string>();
            var sourceHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var rows = new List<Row>();
            var clones = new Dictionary<Mesh, Mesh>();
            var saved = new List<SourceState>();
            string backup = Path.Combine(Output, "main-before.bytes");
            File.Copy(Main, backup, true);
            string mainHash = Hash(Main);
            var originalFiles = new HashSet<string>(Directory.GetFiles("Assets", "*", SearchOption.AllDirectories),
                StringComparer.Ordinal);
            bool generatedParentExisted = Directory.Exists("Assets/AarisWorld/Generated");
            Scene scene = default;
            try
            {
                scene = EditorSceneManager.OpenScene(Main, OpenSceneMode.Single);
                foreach (GameObject root in scene.GetRootGameObjects())
                    Require(!root.name.StartsWith(HostPrefix, StringComparison.Ordinal), "baked-host-already-present");
                string physicsBefore = PhysicsSignature(scene);
                report.authoredLightProbeCount = LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : 0;
                foreach (GameObject root in scene.GetRootGameObjects())
                    report.authoredReflectionProbeCount += root.GetComponentsInChildren<ReflectionProbe>(true).Length;
                hasReflectionProbes = report.authoredReflectionProbeCount > 0;
                var lod = new HashSet<Renderer>();
                foreach (GameObject root in scene.GetRootGameObjects())
                    foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
                        foreach (LOD level in group.GetLODs())
                            foreach (Renderer renderer in level.renderers)
                                if (renderer != null) lod.Add(renderer);
                var candidates = new List<MeshRenderer>();
                foreach (GameObject root in scene.GetRootGameObjects())
                    candidates.AddRange(root.GetComponentsInChildren<MeshRenderer>(true));
                candidates.Sort((a, b) => string.CompareOrdinal(Hierarchy(a.transform), Hierarchy(b.transform)));
                var cohorts = new SortedDictionary<string, Cohort>(StringComparer.Ordinal);
                foreach (MeshRenderer renderer in candidates)
                {
                    report.considered++;
                    if (lod.Contains(renderer) || renderer.GetComponentInParent<LODGroup>(true) != null)
                    { report.excludedLod++; continue; }
                    if (!EligibleState(renderer)) { report.excludedState++; continue; }
                    Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                    if (!EligibleMaterials(renderer, mesh)) { report.excludedMaterial++; continue; }
                    string fallback = FallbackPath(renderer);
                    Vector3 center = renderer.bounds.center;
                    string key = fallback.Length != 0 ? "fallback:" + fallback :
                        "persistent:" + Mathf.FloorToInt(center.x / CellSize) + ":" + Mathf.FloorToInt(center.z / CellSize);
                    if (!cohorts.TryGetValue(key, out Cohort cohort))
                    { cohort = new Cohort { key = key, fallback = fallback }; cohorts.Add(key, cohort); }
                    cohort.sources.Add(renderer);
                    report.eligible++;
                    ProtectAsset(mesh, sourceHashes);
                    foreach (Material material in renderer.sharedMaterials) ProtectAsset(material, sourceHashes);
                    saved.Add(new SourceState { renderer = renderer, mesh = mesh, enabled = renderer.enabled,
                        forceOff = renderer.forceRenderingOff, matrix = renderer.localToWorldMatrix,
                        materials = renderer.sharedMaterials });
                }
                EnsureDirectory("Assets/AarisWorld/Generated");
                EnsureDirectory(Generated);
                foreach (Cohort cohort in cohorts.Values)
                {
                    if (cohort.sources.Count < 2) continue;
                    var groups = new SortedDictionary<string, MaterialGroup>(StringComparer.Ordinal);
                    int inputDraws = 0;
                    Bounds bounds = cohort.sources[0].bounds;
                    foreach (MeshRenderer renderer in cohort.sources)
                    {
                        bounds.Encapsulate(renderer.bounds);
                        Mesh source = renderer.GetComponent<MeshFilter>().sharedMesh;
                        if (!clones.TryGetValue(source, out Mesh readable))
                        { readable = ReadableCopy(source); clones.Add(source, readable); }
                        Material[] materials = renderer.sharedMaterials;
                        for (int sub = 0; sub < source.subMeshCount; sub++)
                        {
                            if (source.GetIndexCount(sub) == 0) continue;
                            inputDraws++;
                            string key = MaterialKey(renderer, source, materials[sub]);
                            if (!groups.TryGetValue(key, out MaterialGroup group))
                            { group = new MaterialGroup { key = key, exemplar = renderer, material = materials[sub] };
                                groups.Add(key, group); }
                            group.parts.Add(new Part { renderer = renderer, mesh = readable, submesh = sub });
                        }
                    }
                    Vector3 origin = bounds.center;
                    var built = new List<BuiltMesh>();
                    foreach (MaterialGroup group in groups.Values) BuildMeshes(group, origin, built);
                    if (built.Count >= inputDraws)
                    { foreach (BuiltMesh item in built) Object.DestroyImmediate(item.mesh); continue; }
                    string suffix = rows.Count.ToString("D3");
                    var host = new GameObject(HostPrefix + suffix);
                    SceneManager.MoveGameObjectToScene(host, scene);
                    host.transform.position = origin;
                    var outputs = new List<MeshRenderer>();
                    var assets = new List<string>();
                    int vertices = 0; long indices = 0;
                    for (int i = 0; i < built.Count; i++)
                    {
                        BuiltMesh item = built[i];
                        string path = Generated + "/Cohort_" + suffix + "_" + i.ToString("D3") + ".asset";
                        Require(!File.Exists(path), "generated-mesh-exists");
                        item.mesh.name = "AarisCohort_" + suffix + "_" + i.ToString("D3");
                        vertices += item.mesh.vertexCount;
                        indices += item.mesh.GetIndexCount(0);
                        AssetDatabase.CreateAsset(item.mesh, path);
                        generated.Add(path); assets.Add(path);
                        var child = new GameObject("Material_" + i.ToString("D3"));
                        child.transform.SetParent(host.transform, false);
                        child.layer = item.group.exemplar.gameObject.layer;
                        GameObjectUtility.SetStaticEditorFlags(child, (StaticEditorFlags)0);
                        child.AddComponent<MeshFilter>().sharedMesh = item.mesh;
                        MeshRenderer output = child.AddComponent<MeshRenderer>();
                        CopyRendererState(item.group.exemplar, output);
                        output.sharedMaterial = item.group.material;
                        output.enabled = false;
                        outputs.Add(output);
                    }
                    host.AddComponent<AarisBakedStaticBatch>().Configure(cohort.sources.ToArray(),
                        outputs.ToArray(), cohort.fallback, inputDraws);
                    rows.Add(new Row { key = cohort.key, scene = scene.path, fallbackScene = cohort.fallback,
                        host = host.name, sources = cohort.sources.Count, inputSubmeshes = inputDraws,
                        outputs = outputs.Count, vertices = vertices, indices = indices,
                        sourceHierarchies = cohort.sources.ConvertAll(r => Hierarchy(r.transform)).ToArray(),
                        sourceMatrices = cohort.sources.ConvertAll(r => r.transform.localToWorldMatrix).ToArray(),
                        outputMatrices = outputs.ConvertAll(r => r.transform.localToWorldMatrix).ToArray(),
                        assets = assets.ToArray() });
                    report.sourceRenderers += cohort.sources.Count;
                    report.sourceSubmeshes += inputDraws; report.combinedRenderers += outputs.Count;
                    report.vertices += vertices; report.indices += indices;
                }
                Require(rows.Count > 0, "no-beneficial-cohorts");
                ValidateSourceStates(saved);
                Require(physicsBefore == PhysicsSignature(scene), "physics-changed");
                foreach (var proof in sourceHashes) Require(Hash(proof.Key) == proof.Value, "source-asset-changed");
                AssetDatabase.SaveAssets();
                Require(EditorSceneManager.SaveScene(scene), "save-scene-failed");
                scene = EditorSceneManager.OpenScene(Main, OpenSceneMode.Single);
                int hosts = 0, outputsAfter = 0;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (!root.name.StartsWith(HostPrefix, StringComparison.Ordinal)) continue;
                    hosts++;
                    var cohort = root.GetComponent<AarisBakedStaticBatch>();
                    Require(cohort != null, "reloaded-cohort-missing");
                    Row expected = rows.Find(row => row.host == root.name);
                    Require(expected != null, "unexpected-reloaded-cohort");
                    ValidateReloadedCohort(cohort, expected);
                    foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        Require(!renderer.enabled && !renderer.forceRenderingOff, "saved-output-must-be-disabled");
                        Require(renderer.GetComponent<MeshFilter>().sharedMesh != null && renderer.sharedMaterial != null,
                            "generated-reference-missing");
                        Require(((int)GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & 4) == 0,
                            "generated-output-must-not-auto-static-batch");
                        outputsAfter++;
                    }
                }
                Require(hosts == rows.Count && outputsAfter == report.combinedRenderers, "reloaded-count-mismatch");
                Require(physicsBefore == PhysicsSignature(scene), "reloaded-physics-changed");
                foreach (var proof in sourceHashes) Require(Hash(proof.Key) == proof.Value, "source-asset-changed-after-save");
                report.status = "PASS"; report.reason = "EDITOR_ONLY_DERIVED_GEOMETRY_SAVED_AND_REOPENED";
                report.cohorts = rows.Count; report.rows = rows.ToArray();
                report.protectedSourceBytesUnchanged = report.originalRendererStateUnchanged = true;
                report.sourcePhysicsUnchanged = report.savedOutputsDisabled = report.reloadedReferencesValid = true;
                var proofs = new List<AssetProof>();
                foreach (var proof in sourceHashes) proofs.Add(Proof(proof.Key));
                report.sourceAssets = proofs.ToArray(); proofs.Clear();
                foreach (string path in generated) { proofs.Add(Proof(path)); proofs.Add(Proof(path + ".meta")); }
                report.generatedAssets = proofs.ToArray();
                File.WriteAllText(Path.Combine(Output, "report.json"), JsonUtility.ToJson(report, true));
                Debug.Log("AARIS_STATIC_COHORT_BAKE_PASS cohorts=" + rows.Count + " source_renderers=" +
                    report.sourceRenderers + " source_submeshes=" + report.sourceSubmeshes +
                    " generated_renderers=" + report.combinedRenderers + " vertices=" + report.vertices);
            }
            catch (Exception error)
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                File.Copy(backup, Main, true);
                foreach (string path in generated) if (File.Exists(path)) AssetDatabase.DeleteAsset(path);
                if (Directory.Exists(Generated)) AssetDatabase.DeleteAsset(Generated);
                string generatedMeta = Generated + ".meta";
                if (!originalFiles.Contains(generatedMeta) && File.Exists(generatedMeta)) File.Delete(generatedMeta);
                if (!generatedParentExisted && Directory.Exists("Assets/AarisWorld/Generated") &&
                    Directory.GetFileSystemEntries("Assets/AarisWorld/Generated").Length == 0)
                    AssetDatabase.DeleteAsset("Assets/AarisWorld/Generated");
                AssetDatabase.Refresh();
                Require(Hash(Main) == mainHash, "rollback-scene-hash-mismatch");
                report.status = "FAIL_ROLLED_BACK"; report.reason = error.ToString();
                File.WriteAllText(Path.Combine(Output, "report.json"), JsonUtility.ToJson(report, true));
                throw;
            }
            finally
            {
                foreach (Mesh clone in clones.Values) if (clone != null) Object.DestroyImmediate(clone);
            }
        }

        private static bool EligibleState(MeshRenderer renderer)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                renderer.forceRenderingOff || renderer.HasPropertyBlock()) return false;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null || filter.sharedMesh.vertexCount == 0 ||
                filter.sharedMesh.vertexCount > VertexLimit || filter.sharedMesh.blendShapeCount != 0) return false;
            if (((int)GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & (1 | 2 | 4 | 8 | 16)) == 0)
                return false;
            Matrix4x4 authoredMatrix = renderer.localToWorldMatrix;
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(authoredMatrix[i]) || float.IsInfinity(authoredMatrix[i])) return false;
            if (renderer.localToWorldMatrix.determinant <= 0f || renderer.probeAnchor != null ||
                renderer.lightProbeProxyVolumeOverride != null ||
                (renderer.lightmapIndex >= 0 && renderer.lightmapIndex < 65534) ||
                (renderer.realtimeLightmapIndex >= 0 && renderer.realtimeLightmapIndex < 65534)) return false;
            if (LightmapSettings.lightProbes != null && LightmapSettings.lightProbes.count > 0 &&
                renderer.lightProbeUsage != LightProbeUsage.Off) return false;
            if (hasReflectionProbes && renderer.reflectionProbeUsage != ReflectionProbeUsage.Off) return false;
            for (Transform node = renderer.transform; node != null; node = node.parent)
            {
                if (node.GetComponent<Rigidbody>() != null || node.GetComponent<Animator>() != null ||
                    node.GetComponent<CharacterController>() != null) return false;
                foreach (MonoBehaviour script in node.GetComponents<MonoBehaviour>())
                    if (script == null || !(script is AarisChunkFallbackVisibility)) return false;
            }
            foreach (Component component in renderer.GetComponentsInChildren<Component>(true))
                if (component == null || component is MonoBehaviour || component is Rigidbody ||
                    component is Animator || component is CharacterController || component is SkinnedMeshRenderer)
                    return false;
            return true;
        }

        private static bool EligibleMaterials(MeshRenderer renderer, Mesh mesh)
        {
            Material[] materials = renderer.sharedMaterials;
            if (materials.Length != mesh.subMeshCount || materials.Length == 0) return false;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (mesh.GetIndexCount(i) == 0 || mesh.GetTopology(i) != MeshTopology.Triangles || material == null || material.shader == null ||
                    !material.shader.isSupported || material.renderQueue >= (int)RenderQueue.AlphaTest ||
                    material.GetTag("DisableBatching", false, "False") != "False" ||
                    material.GetTag("RenderType", true, "Opaque") != "Opaque") return false;
                string shader = material.shader.name;
                if (shader != "Universal Render Pipeline/Lit" && shader != "Universal Render Pipeline/Simple Lit" &&
                    shader != "Universal Render Pipeline/Unlit" && shader != "Universal Render Pipeline/Baked Lit") return false;
                if ((material.HasProperty("_Surface") && material.GetFloat("_Surface") != 0f) ||
                    (material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") != 0f)) return false;
            }
            return true;
        }

        private static string FallbackPath(MeshRenderer renderer)
        {
            AarisChunkFallbackVisibility fallback = renderer.GetComponentInParent<AarisChunkFallbackVisibility>(true);
            if (fallback == null) return "";
            var serialized = new SerializedObject(fallback);
            string path = serialized.FindProperty("chunkScenePath").stringValue;
            Require(!string.IsNullOrEmpty(path), "fallback-without-scene-owner");
            SerializedProperty sources = serialized.FindProperty("silhouetteRenderers");
            bool found = false;
            for (int i = 0; i < sources.arraySize; i++)
                if (sources.GetArrayElementAtIndex(i).objectReferenceValue == renderer) found = true;
            Require(found, "fallback-renderer-not-owned-by-visibility-array");
            return path;
        }

        private static string MaterialKey(MeshRenderer r, Mesh mesh, Material material)
        {
            Require(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(material, out string guid, out long localId),
                "material-identity-not-persistent");
            string key = guid + ":" + localId + ":" + r.gameObject.layer + ":" + (int)r.shadowCastingMode +
                ":" + r.receiveShadows + ":" + r.renderingLayerMask + ":" + (int)r.lightProbeUsage +
                ":" + (int)r.reflectionProbeUsage + ":" + (int)r.motionVectorGenerationMode +
                ":" + r.allowOcclusionWhenDynamic + ":" + r.sortingLayerID + ":" + r.sortingOrder;
            foreach (VertexAttributeDescriptor attribute in mesh.GetVertexAttributes())
                key += ":" + attribute.attribute + "/" + attribute.format + "/" + attribute.dimension + "/" + attribute.stream;
            return key;
        }

        private static Mesh ReadableCopy(Mesh source)
        {
            using (Mesh.MeshDataArray input = MeshUtility.AcquireReadOnlyMeshData(source))
            {
                Mesh.MeshData src = input[0];
                Mesh.MeshDataArray output = Mesh.AllocateWritableMeshData(1);
                try
                {
                    Mesh.MeshData dst = output[0];
                    dst.SetVertexBufferParams(src.vertexCount, source.GetVertexAttributes());
                    for (int stream = 0; stream < src.vertexBufferCount; stream++)
                        dst.GetVertexData<byte>(stream).CopyFrom(src.GetVertexData<byte>(stream));
                    if (src.indexFormat == IndexFormat.UInt16)
                    {
                        NativeArray<ushort> indices = src.GetIndexData<ushort>();
                        dst.SetIndexBufferParams(indices.Length, IndexFormat.UInt16);
                        dst.GetIndexData<ushort>().CopyFrom(indices);
                    }
                    else
                    {
                        NativeArray<uint> indices = src.GetIndexData<uint>();
                        dst.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
                        dst.GetIndexData<uint>().CopyFrom(indices);
                    }
                    dst.subMeshCount = src.subMeshCount;
                    for (int i = 0; i < src.subMeshCount; i++)
                        dst.SetSubMesh(i, src.GetSubMesh(i), MeshUpdateFlags.DontRecalculateBounds |
                            MeshUpdateFlags.DontValidateIndices);
                    var mesh = new Mesh { name = source.name + "_EditorReadableCopy" };
                    Mesh.ApplyAndDisposeWritableMeshData(output, mesh, MeshUpdateFlags.DontRecalculateBounds |
                        MeshUpdateFlags.DontValidateIndices);
                    output = default;
                    mesh.bounds = source.bounds;
                    Require(mesh.vertexCount == source.vertexCount && mesh.subMeshCount == source.subMeshCount,
                        "readable-copy-layout-mismatch");
                    for (int i = 0; i < source.subMeshCount; i++)
                        Require(mesh.GetIndexCount(i) == source.GetIndexCount(i), "readable-copy-index-mismatch");
                    return mesh;
                }
                finally { if (output.Length != 0) output.Dispose(); }
            }
        }

        private static void BuildMeshes(MaterialGroup group, Vector3 origin, List<BuiltMesh> results)
        {
            var instances = new List<CombineInstance>();
            int vertices = 0; ulong expectedIndices = 0;
            Matrix4x4 toLocal = Matrix4x4.Translate(-origin);
            foreach (Part part in group.parts)
            {
                if (instances.Count > 0 && vertices + part.mesh.vertexCount > VertexLimit)
                {
                    FlushMesh(group, instances, expectedIndices, results);
                    instances.Clear(); vertices = 0; expectedIndices = 0;
                }
                instances.Add(new CombineInstance { mesh = part.mesh, subMeshIndex = part.submesh,
                    transform = toLocal * part.renderer.localToWorldMatrix });
                vertices += part.mesh.vertexCount; expectedIndices += part.mesh.GetIndexCount(part.submesh);
            }
            if (instances.Count > 0) FlushMesh(group, instances, expectedIndices, results);
        }

        private static void FlushMesh(MaterialGroup group, List<CombineInstance> parts, ulong expectedIndices,
            List<BuiltMesh> results)
        {
            var mesh = new Mesh { indexFormat = IndexFormat.UInt16 };
            mesh.CombineMeshes(parts.ToArray(), true, true, false);
            mesh.RecalculateBounds();
            Require(mesh.vertexCount > 0 && mesh.vertexCount <= VertexLimit && mesh.subMeshCount == 1 &&
                mesh.GetIndexCount(0) == expectedIndices, "combined-geometry-count-mismatch");
            results.Add(new BuiltMesh { mesh = mesh, group = group });
        }

        private static void CopyRendererState(MeshRenderer source, MeshRenderer output)
        {
            output.shadowCastingMode = source.shadowCastingMode;
            output.receiveShadows = source.receiveShadows;
            output.renderingLayerMask = source.renderingLayerMask;
            output.lightProbeUsage = source.lightProbeUsage;
            output.reflectionProbeUsage = source.reflectionProbeUsage;
            output.motionVectorGenerationMode = source.motionVectorGenerationMode;
            output.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            output.sortingLayerID = source.sortingLayerID;
            output.sortingOrder = source.sortingOrder;
            output.lightmapIndex = -1; output.realtimeLightmapIndex = -1;
        }

        private static void ValidateSourceStates(List<SourceState> states)
        {
            foreach (SourceState state in states)
            {
                Require(state.renderer != null && state.renderer.enabled == state.enabled &&
                    state.renderer.forceRenderingOff == state.forceOff &&
                    state.renderer.localToWorldMatrix == state.matrix &&
                    state.renderer.GetComponent<MeshFilter>().sharedMesh == state.mesh, "source-renderer-state-changed");
                Material[] current = state.renderer.sharedMaterials;
                Require(current.Length == state.materials.Length, "source-material-count-changed");
                for (int i = 0; i < current.Length; i++) Require(current[i] == state.materials[i], "source-material-changed");
            }
        }

        private static void ValidateReloadedCohort(AarisBakedStaticBatch cohort, Row expected)
        {
            var serialized = new SerializedObject(cohort);
            SerializedProperty sources = serialized.FindProperty("sources");
            SerializedProperty outputs = serialized.FindProperty("outputs");
            Require(sources != null && sources.arraySize == expected.sources &&
                outputs != null && outputs.arraySize == expected.outputs &&
                serialized.FindProperty("fallbackScenePath").stringValue == expected.fallbackScene &&
                serialized.FindProperty("sourceSubmeshDraws").intValue == expected.inputSubmeshes,
                "reloaded-cohort-serialized-count-or-owner-mismatch");
            const BindingFlags privateFields = BindingFlags.Instance | BindingFlags.NonPublic;
            var storedSources = (Array)typeof(AarisBakedStaticBatch).GetField("sources", privateFields).GetValue(cohort);
            var storedOutputMatrices = (Matrix4x4[])typeof(AarisBakedStaticBatch).GetField("outputMatrices", privateFields).GetValue(cohort);
            SerializedProperty outputMaterials = serialized.FindProperty("outputMaterials");
            Require(storedSources.Length == expected.sources && storedOutputMatrices.Length == expected.outputs &&
                outputMaterials.arraySize == expected.outputs, "reloaded-pose-snapshot-count-mismatch");
            int sourceDraws = 0;
            for (int i = 0; i < sources.arraySize; i++)
            {
                SerializedProperty source = sources.GetArrayElementAtIndex(i);
                MeshRenderer renderer = source.FindPropertyRelative("renderer").objectReferenceValue as MeshRenderer;
                Require(renderer != null && renderer.gameObject.scene == cohort.gameObject.scene &&
                    Hierarchy(renderer.transform) == expected.sourceHierarchies[i] &&
                    renderer.enabled && !renderer.forceRenderingOff, "reloaded-cohort-source-reference-mismatch");
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Require(filter != null && filter.sharedMesh != null &&
                    source.FindPropertyRelative("authoredMesh").objectReferenceValue == filter.sharedMesh,
                    "reloaded-cohort-source-mesh-reference-mismatch");
                object stored = storedSources.GetValue(i);
                Matrix4x4 storedMatrix = (Matrix4x4)stored.GetType().GetField("localToWorld").GetValue(stored);
                Require(SameMatrix(renderer.transform.localToWorldMatrix, expected.sourceMatrices[i]) &&
                    SameMatrix(storedMatrix, expected.sourceMatrices[i]), "reloaded-source-pose-or-snapshot-mismatch");
                SerializedProperty materials = source.FindPropertyRelative("materials");
                Material[] current = renderer.sharedMaterials;
                sourceDraws += current.Length;
                Require(materials.arraySize == current.Length, "reloaded-cohort-source-material-count-mismatch");
                for (int material = 0; material < current.Length; material++)
                    Require(materials.GetArrayElementAtIndex(material).objectReferenceValue == current[material],
                        "reloaded-cohort-source-material-reference-mismatch");
            }
            Require(sourceDraws == expected.inputSubmeshes, "reloaded-source-draw-coverage-mismatch");
            for (int i = 0; i < outputs.arraySize; i++)
            {
                MeshRenderer renderer = outputs.GetArrayElementAtIndex(i).objectReferenceValue as MeshRenderer;
                Require(renderer != null && renderer.transform.parent == cohort.transform &&
                    renderer.gameObject.scene == cohort.gameObject.scene &&
                    AssetDatabase.GetAssetPath(renderer.GetComponent<MeshFilter>().sharedMesh) == expected.assets[i],
                    "reloaded-cohort-output-reference-mismatch");
                Require(SameMatrix(renderer.transform.localToWorldMatrix, expected.outputMatrices[i]) &&
                    SameMatrix(storedOutputMatrices[i], expected.outputMatrices[i]) &&
                    outputMaterials.GetArrayElementAtIndex(i).objectReferenceValue == renderer.sharedMaterial,
                    "reloaded-output-pose-or-material-snapshot-mismatch");
            }
        }

        private static bool SameMatrix(Matrix4x4 first, Matrix4x4 second)
        {
            for (int i = 0; i < 16; i++)
                if (float.IsNaN(first[i]) || float.IsInfinity(first[i]) ||
                    float.IsNaN(second[i]) || float.IsInfinity(second[i]) ||
                    Mathf.Abs(first[i] - second[i]) > .0005f) return false;
            return true;
        }

        private static string PhysicsSignature(Scene scene)
        {
            var rows = new List<string>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                {
                    string row = Hierarchy(collider.transform) + ":" + collider.GetType().FullName + ":" +
                        collider.enabled + ":" + collider.isTrigger + ":" + Number(collider.contactOffset) + ":" +
                        AssetIdentity(collider.sharedMaterial) + ":" + collider.includeLayers.value + ":" +
                        collider.excludeLayers.value + ":" + collider.layerOverridePriority + ":" +
                        collider.gameObject.layer + ":" + collider.gameObject.activeSelf;
                    Matrix4x4 matrix = collider.transform.localToWorldMatrix;
                    for (int i = 0; i < 16; i++) row += ":" + Number(matrix[i]);
                    if (collider is MeshCollider mesh)
                        row += ":" + AssetIdentity(mesh.sharedMesh) + ":" + mesh.convex + ":" + (int)mesh.cookingOptions;
                    else if (collider is BoxCollider box) row += ":" + Vector(box.center) + ":" + Vector(box.size);
                    else if (collider is CapsuleCollider capsule)
                        row += ":" + Vector(capsule.center) + ":" + Number(capsule.radius) + ":" +
                            Number(capsule.height) + ":" + capsule.direction;
                    else if (collider is SphereCollider sphere) row += ":" + Vector(sphere.center) + ":" + Number(sphere.radius);
                    else if (collider is TerrainCollider terrainCollider) row += ":" + AssetIdentity(terrainCollider.terrainData);
                    else if (collider is CharacterController controller)
                        row += ":" + Vector(controller.center) + ":" + Number(controller.radius) + ":" +
                            Number(controller.height) + ":" + Number(controller.slopeLimit) + ":" +
                            Number(controller.stepOffset) + ":" + Number(controller.skinWidth) + ":" +
                            Number(controller.minMoveDistance);
                    else throw new InvalidOperationException("Unreviewed collider type: " + collider.GetType().FullName);
                    rows.Add(row);
                }
                foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                    rows.Add(Hierarchy(terrain.transform) + ":Terrain:" + AssetDatabase.GetAssetPath(terrain.terrainData));
            }
            rows.Sort(StringComparer.Ordinal);
            return string.Join("\n", rows);
        }

        private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string Vector(Vector3 value) => Number(value.x) + "," + Number(value.y) + "," + Number(value.z);
        private static string AssetIdentity(Object value)
        {
            if (value == null) return "null";
            Require(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long id),
                "physics-reference-not-persistent");
            return guid + ":" + id;
        }

        private static void ProtectAsset(Object asset, SortedDictionary<string, string> hashes)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            if (path.Length == 0 || !File.Exists(path)) return;
            if (!hashes.ContainsKey(path)) hashes.Add(path, Hash(path));
            if (File.Exists(path + ".meta") && !hashes.ContainsKey(path + ".meta")) hashes.Add(path + ".meta", Hash(path + ".meta"));
        }
        private static void EnsureDirectory(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureDirectory(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
        private static string Hierarchy(Transform node)
        {
            string result = node.name;
            for (Transform parent = node.parent; parent != null; parent = parent.parent) result = parent.name + "/" + result;
            return result;
        }
        private static AssetProof Proof(string path) => new AssetProof { path = path, sha256 = Hash(path), bytes = new FileInfo(path).Length };
        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        private static void Require(bool valid, string reason)
        { if (!valid) throw new InvalidOperationException("AARIS_STATIC_BATCH: " + reason); }
    }
}
#endif
