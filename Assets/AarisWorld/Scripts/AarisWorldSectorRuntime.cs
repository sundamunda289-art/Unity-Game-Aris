using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview
{
    /// <summary>
    /// The 150m world grid owns static visual detail only. The existing nearby
    /// collision index and persistent TerrainData remain the floor authority.
    /// Loaded district roots outside the active 1-ring use their original last
    /// LOD until their additive scene retires, so silhouettes never disappear.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(-94)]
    public sealed class AarisWorldSectorRuntime : MonoBehaviour
    {
        private const string MainScene = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const int MaximumVerticesPerBatch = 48000;
        private const int MaximumBatchVertices = 200000;
        private const int MaximumLodTransitionsPerFrame = 24;
        private static AarisWorldSectorRuntime active;
        private sealed class LodEntry
        {
            internal LODGroup group;
            internal Bounds bounds;
            internal Vector3 referencePoint;
            internal float size, cullThreshold;
            internal int appliedLevel = -1;
            internal int farLevel, sceneHandle, instanceId, version = -1;
            internal bool macro;
        }
        private sealed class Batch
        {
            internal int sceneHandle, vertices;
            internal readonly List<GameObject> members = new List<GameObject>(16);
        }
        private readonly List<LodEntry> lods = new List<LodEntry>(256);
        private readonly HashSet<int> registeredLods = new HashSet<int>();
        private readonly HashSet<int> registeredRenderers = new HashSet<int>();
        private readonly Dictionary<int, List<int>> sceneRendererIds = new Dictionary<int, List<int>>();
        private readonly Dictionary<int, int> sceneBatchVertices = new Dictionary<int, int>();
        private readonly Dictionary<int, (int batched, int unreadable)> sceneRendererCounts =
            new Dictionary<int, (int batched, int unreadable)>();
        private readonly List<GameObject> sceneRoots = new List<GameObject>(128);
        private readonly List<LODGroup> scratchLods = new List<LODGroup>(128);
        private readonly List<AarisChunkFallbackVisibility> scratchFallbacks =
            new List<AarisChunkFallbackVisibility>(128);
        private readonly List<MeshRenderer> scratchRenderers = new List<MeshRenderer>(256);
        private readonly List<Component> scratchComponents = new List<Component>(64);
        private readonly List<MonoBehaviour> scratchScripts = new List<MonoBehaviour>(8);
        private readonly List<Batch> pendingBatches = new List<Batch>(64);
        private readonly Dictionary<(int scene, int x, int z), Batch> cellBatches =
            new Dictionary<(int scene, int x, int z), Batch>();
        private AarisThirdPersonPlayer player;
        private AarisOfflineDrop drop;
        private int previousX = int.MinValue, previousZ = int.MinValue;
        private int version, cursor, nextBatch, committedVertices;
        private bool previousFlight;
        private Vector3 previousViewPosition;
        private float previousFov, previousLodBias;
        public int StaticLodCount => lods.Count;
        public int MacroLodCount { get; private set; }
        public int BatchedRendererCount { get; private set; }
        public int UnreadableRendererCount { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            Scene active = SceneManager.GetActiveScene();
            if (active.path != MainScene || FindFirstObjectByType<AarisWorldSectorRuntime>() != null)
                return;
            new GameObject("__Aaris150mWorldSectors").AddComponent<AarisWorldSectorRuntime>();
        }

        private void Awake()
        {
            active = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            for (int i = 0; i < SceneManager.sceneCount; i++)
                RegisterScene(SceneManager.GetSceneAt(i));
        }

        private void Start() => BindPlayer();

        private void BindPlayer()
        {
            if (player == null) player = FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (player != null && drop == null) drop = player.GetComponent<AarisOfflineDrop>();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            RegisterScene(scene);
            BindPlayer();
        }

        internal static void RegisterRuntimeStaticRoot(GameObject root)
        {
            if (active == null || root == null) return;
            active.RegisterRoot(root, root.scene);
            active.SealBatchRegistration();
        }

        private void RegisterScene(Scene scene)
        {
            if (!scene.isLoaded || (scene.path != MainScene &&
                !scene.path.StartsWith("Assets/AarisWorld/Scenes/Chunks/", StringComparison.Ordinal))) return;
            sceneRoots.Clear();
            scene.GetRootGameObjects(sceneRoots);
            for (int r = 0; r < sceneRoots.Count; r++)
                RegisterRoot(sceneRoots[r], scene);
            SealBatchRegistration();
        }

        private void RegisterRoot(GameObject root, Scene scene)
        {
            scratchLods.Clear();
            root.GetComponentsInChildren(true, scratchLods);
            for (int i = 0; i < scratchLods.Count; i++)
            {
                LODGroup group = scratchLods[i];
                if (group == null || !group.enabled || registeredLods.Contains(group.GetInstanceID()) ||
                    !IsStaticVisual(group.gameObject)) continue;
                LOD[] levels = group.GetLODs();
                if (levels.Length < 2) continue;
                bool haveBounds = false;
                Bounds bounds = default;
                for (int level = 0; level < levels.Length; level++)
                    for (int n = 0; n < levels[level].renderers.Length; n++)
                    {
                        Renderer renderer = levels[level].renderers[n];
                        if (renderer == null) continue;
                        if (!haveBounds) { bounds = renderer.bounds; haveBounds = true; }
                        else bounds.Encapsulate(renderer.bounds);
                        // Warm the macro view first; do not exhaust the
                        // vertex budget on LOD0 meshes hidden during flight.
                        if (level == levels.Length - 1 && renderer is MeshRenderer mesh)
                            AddBatchCandidate(mesh, scene.handle);
                    }
                if (!haveBounds || levels[levels.Length - 1].renderers.Length == 0) continue;
                registeredLods.Add(group.GetInstanceID());
                Vector3 scale = group.transform.lossyScale;
                lods.Add(new LodEntry { group = group, bounds = bounds,
                    sceneHandle = scene.handle, instanceId = group.GetInstanceID(),
                    referencePoint = group.transform.TransformPoint(group.localReferencePoint),
                    size = group.size * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)),
                    cullThreshold = levels[levels.Length - 1].screenRelativeTransitionHeight,
                    farLevel = levels.Length - 1 });
            }
            // The explicit fallback component marks authored static
            // silhouettes. Do not sweep vehicles, actors or interactive doors.
            scratchFallbacks.Clear();
            root.GetComponentsInChildren(true, scratchFallbacks);
            for (int i = 0; i < scratchFallbacks.Count; i++)
            {
                scratchRenderers.Clear();
                scratchFallbacks[i].CollectSectorBatchRenderers(scratchRenderers);
                for (int n = 0; n < scratchRenderers.Count; n++)
                    AddBatchCandidate(scratchRenderers[n], scene.handle);
            }
        }

        private void SealBatchRegistration()
        {
            // Do not append to an already queued/combined batch after a scene
            // load. A fresh group retains the exact loaded-scene ownership.
            cellBatches.Clear();
            if (nextBatch > 0)
            { pendingBatches.RemoveRange(0, nextBatch); nextBatch = 0; }
        }

        private bool IsStaticVisual(GameObject root)
        {
            // A vehicle/character's LOD root can itself contain only meshes;
            // its motion authority often lives on a parent GameObject.
            for (Transform parent = root.transform; parent != null; parent = parent.parent)
            {
                if (parent.GetComponent<Rigidbody>() != null || parent.GetComponent<Animator>() != null)
                    return false;
                scratchScripts.Clear();
                parent.GetComponents(scratchScripts);
                for (int i = 0; i < scratchScripts.Count; i++)
                    if (!(scratchScripts[i] is AarisSettlementExpansion ||
                        scratchScripts[i] is AarisOwnedMeshCleanup ||
                        scratchScripts[i] is AarisSectorBatchLifetime)) return false;
            }
            scratchComponents.Clear();
            root.GetComponentsInChildren(true, scratchComponents);
            for (int i = 0; i < scratchComponents.Count; i++)
            {
                Component component = scratchComponents[i];
                // Static authored mesh cover may share a LOD root. Its native
                // collider is untouched; any script/Animator/Rigidbody fails open.
                if (!(component is Transform || component is MeshFilter || component is MeshRenderer ||
                    component is LODGroup || component is MeshCollider || component is BoxCollider ||
                    component is AarisOwnedMeshCleanup))
                    return false;
            }
            return true;
        }

        private void AddBatchCandidate(MeshRenderer renderer, int sceneHandle)
        {
            if (renderer == null || renderer.isPartOfStaticBatch ||
                !registeredRenderers.Add(renderer.GetInstanceID())) return;
            if (!sceneRendererIds.TryGetValue(sceneHandle, out List<int> identifiers))
            { identifiers = new List<int>(); sceneRendererIds.Add(sceneHandle, identifiers); }
            identifiers.Add(renderer.GetInstanceID());
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null || mesh.vertexCount == 0 || mesh.vertexCount > MaximumVerticesPerBatch) return;
            if (!mesh.isReadable)
            {
                UnreadableRendererCount++;
                sceneRendererCounts.TryGetValue(sceneHandle, out var counts);
                sceneRendererCounts[sceneHandle] = (counts.batched, counts.unreadable + 1);
                return;
            }
            // A public API cannot safely batch an unsupported shader or
            // property-block override. Leave that renderer entirely unchanged.
            if (renderer.HasPropertyBlock()) return;
            Material[] materials = renderer.sharedMaterials;
            if (materials.Length == 0) return;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null || material.shader == null || !material.shader.isSupported ||
                    material.GetTag("DisableBatching", false, "False") != "False") return;
            }
            Vector3 position = renderer.bounds.center;
            var key = (sceneHandle, AarisWorldSectorPolicy.Cell(position.x),
                AarisWorldSectorPolicy.Cell(position.z));
            if (!cellBatches.TryGetValue(key, out Batch batch) ||
                batch.vertices + mesh.vertexCount > MaximumVerticesPerBatch)
            {
                batch = new Batch { sceneHandle = sceneHandle };
                cellBatches[key] = batch;
                pendingBatches.Add(batch);
            }
            batch.members.Add(renderer.gameObject);
            batch.vertices += mesh.vertexCount;
        }

        private void Update()
        {
            if (player == null) return;
            Vector3 position = player.transform.position;
            int x = AarisWorldSectorPolicy.Cell(position.x), z = AarisWorldSectorPolicy.Cell(position.z);
            bool flight = drop != null && drop.ShouldDeferWorldDetail;
            if (x != previousX || z != previousZ || flight != previousFlight)
            {
                previousX = x; previousZ = z; previousFlight = flight;
                version++; cursor = 0;
            }
            Camera view = player.AimCamera;
            Vector3 viewPosition = view != null ? view.transform.position : position;
            float fov = view != null ? view.fieldOfView : 60f;
            float lodBias = QualitySettings.lodBias;
            if ((viewPosition - previousViewPosition).sqrMagnitude > 4f ||
                fov != previousFov || lodBias != previousLodBias)
            {
                previousViewPosition = viewPosition; previousFov = fov; previousLodBias = lodBias;
                version++;
            }
            int visited = 0;
            int count = Mathf.Min(lods.Count, MaximumLodTransitionsPerFrame);
            while (visited++ < count)
            {
                if (cursor >= lods.Count) cursor = 0;
                LodEntry entry = lods[cursor++];
                if (entry.group == null || entry.version == version) continue;
                entry.version = version;
                bool macro = flight || !AarisWorldSectorPolicy.IntersectsActiveRing(
                    entry.bounds.center.x, entry.bounds.center.z, entry.bounds.extents.x,
                    entry.bounds.extents.z, position.x, position.z);
                if (entry.macro != macro)
                { entry.macro = macro; MacroLodCount += macro ? 1 : -1; }
                int level = macro ? entry.farLevel : -1;
                // ForceLOD must never resurrect an object below its authored
                // final cull threshold. Give automatic native LOD/culling
                // authority back at that screen size, including scope changes.
                if (macro)
                {
                    float span = view != null && view.orthographic ? view.orthographicSize * 2f :
                        2f * Mathf.Tan(fov * .5f * Mathf.Deg2Rad) *
                            Vector3.Distance(viewPosition, entry.referencePoint);
                    if (view == null || entry.size * lodBias < span * entry.cullThreshold)
                        level = -1;
                }
                if (level == entry.appliedLevel) continue;
                entry.appliedLevel = level;
                entry.group.ForceLOD(level);
            }
            // Native mesh combining is a loading/flight warm-up operation.
            // Never turn a camera rotation or ground firefight into a combine.
            if (drop != null && !drop.HasLanded) BuildOneBatch();
        }

        private void BuildOneBatch()
        {
            if (nextBatch >= pendingBatches.Count) return;
            Batch batch = pendingBatches[nextBatch];
            if (batch.members.Count < 2 || committedVertices + batch.vertices > MaximumBatchVertices)
            { nextBatch++; return; }
            Scene scene = default;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene candidate = SceneManager.GetSceneAt(i);
                if (candidate.handle == batch.sceneHandle) { scene = candidate; break; }
            }
            if (!scene.IsValid() || !scene.isLoaded) { nextBatch++; return; }
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()) return;
#endif
            nextBatch++;
            // Membership allocations occur once during prewarm, never in the
            // steady world loop. Unity retains original renderer-level culling.
            batch.members.RemoveAll(MissingObject);
            if (batch.members.Count < 2) return;
            GameObject[] members = batch.members.ToArray();
            var sourceMeshes = new HashSet<Mesh>();
            for (int i = 0; i < members.Length; i++)
                sourceMeshes.Add(members[i].GetComponent<MeshFilter>().sharedMesh);
            StaticBatchingUtility.Combine(members, null);
            var generated = new List<Mesh>();
            int newlyBatched = 0;
            for (int i = 0; i < members.Length; i++)
            {
                MeshRenderer renderer = members[i].GetComponent<MeshRenderer>();
                if (renderer != null && renderer.isPartOfStaticBatch) newlyBatched++;
                Mesh mesh = members[i].GetComponent<MeshFilter>().sharedMesh;
                if (mesh != null && !sourceMeshes.Contains(mesh) && !generated.Contains(mesh))
                    generated.Add(mesh);
            }
            if (generated.Count > 0)
            {
                var lifetime = new GameObject("__AarisSceneOwnedStaticBatch");
                SceneManager.MoveGameObjectToScene(lifetime, scene);
                lifetime.AddComponent<AarisSectorBatchLifetime>().Initialize(generated.ToArray());
            }
            committedVertices += batch.vertices;
            BatchedRendererCount += newlyBatched;
            sceneRendererCounts.TryGetValue(batch.sceneHandle, out var counts);
            sceneRendererCounts[batch.sceneHandle] = (counts.batched + newlyBatched, counts.unreadable);
            sceneBatchVertices.TryGetValue(batch.sceneHandle, out int sceneVertices);
            sceneBatchVertices[batch.sceneHandle] = sceneVertices + batch.vertices;
            batch.members.Clear();
        }

        private static bool MissingObject(GameObject item) => item == null;

        private void OnSceneUnloaded(Scene scene)
        {
            // Grounded play deliberately stops prewarm combining. Discard
            // those unprocessed groups on retirement instead of retaining
            // destroyed renderer wrappers after every driving lap.
            for (int i = pendingBatches.Count - 1; i >= 0; i--)
                if (pendingBatches[i].sceneHandle == scene.handle)
                {
                    pendingBatches.RemoveAt(i);
                    if (i < nextBatch) nextBatch--;
                }
            for (int i = lods.Count - 1; i >= 0; i--)
            {
                LodEntry entry = lods[i];
                if (entry.sceneHandle != scene.handle) continue;
                if (entry.macro) MacroLodCount--;
                registeredLods.Remove(entry.instanceId);
                lods.RemoveAt(i);
            }
            if (sceneRendererIds.TryGetValue(scene.handle, out List<int> identifiers))
            {
                for (int i = 0; i < identifiers.Count; i++) registeredRenderers.Remove(identifiers[i]);
                sceneRendererIds.Remove(scene.handle);
            }
            if (sceneBatchVertices.TryGetValue(scene.handle, out int vertices))
            { committedVertices -= vertices; sceneBatchVertices.Remove(scene.handle); }
            if (sceneRendererCounts.TryGetValue(scene.handle, out var counts))
            {
                BatchedRendererCount -= counts.batched;
                UnreadableRendererCount -= counts.unreadable;
                sceneRendererCounts.Remove(scene.handle);
            }
            cursor = 0;
        }

        private void OnDestroy()
        {
            if (active == this) active = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            for (int i = 0; i < lods.Count; i++)
                if (lods[i].macro && lods[i].group != null) lods[i].group.ForceLOD(-1);
        }
    }

    // Generated static-batch meshes have the same lifetime as their scene.
    // Original source meshes never enter this owner list.
    internal sealed class AarisSectorBatchLifetime : MonoBehaviour
    {
        private Mesh[] owned;
        internal void Initialize(Mesh[] meshes) { owned = meshes; }
        private void OnDestroy()
        {
            if (owned == null) return;
            for (int i = 0; i < owned.Length; i++)
                if (owned[i] != null) Destroy(owned[i]);
            owned = null;
        }
    }
}
