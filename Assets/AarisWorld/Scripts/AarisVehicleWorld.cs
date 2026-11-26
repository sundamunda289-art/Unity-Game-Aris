using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Unity.Profiling;

namespace Aaris.MapReview
{
    [Serializable]
    internal sealed class AarisVehiclePoint
    {
        public string id, kind, district, origin;
        public float x, y, z, yaw;
    }

    [Serializable]
    internal sealed class AarisVehiclePointsFile
    {
        public int schema, sizeMetres, hubCount, roadsideCount;
        public AarisVehiclePoint[] candidates;
    }

    // Cars and parked motorcycles both consume the same authored road plan.
    // Coalesce their Android reads into ONE in-flight Unity resource request.
    // Editor tools retain synchronous Resources.Load and untouched source JSON.
    internal static class AarisVehiclePointManifest
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        private static ResourceRequest pending;
        private static TextAsset cached;

        internal static TextAsset AndroidCached => cached;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            pending = null;
            cached = null;
        }

        internal static IEnumerator LoadAndroidAsync()
        {
            if (cached != null) yield break;
            if (pending == null)
            {
                pending = Resources.LoadAsync<TextAsset>(
                    "AarisVehicles/vehicle_candidates");
                // These are gameplay road positions, not optional decoration:
                // let their tiny document load ahead of cosmetic model imports.
                pending.priority = 0;
            }
            yield return pending;
            if (cached == null) cached = pending.asset as TextAsset;
        }
#endif
    }

    [Serializable]
    internal sealed class AarisVehicleType
    {
        public string id, modelResource, wheelResource;
        public string authoredModelResource, authoredWheelResource;
        // Optional lighter verified body if the primary authored import is
        // unavailable on a constrained/device-specific Unity importer.
        public string fallbackAuthoredModelResource;
        public int weight;
        public float maxSpeedMetres, massKg;
        // Vehicle-class size limits in real-world metres, relative to the
        // 1.78 m player and the 4.7 m roadside parking clearance. Uniform
        // source scaling retains each authored model's true aspect ratio.
        public float referenceWidthMetres, referenceLengthMetres;
        // Declarative physics profiles; unconfigured values fall back safely.
        public string drivetrain;
        public float tyreGripScale, suspensionScale, steeringScale;
        // Optional per-archetype suspension stroke. Zero in older catalogues
        // means neutral 1.0 so older offline saves keep their ride height.
        public float suspensionTravelScale;
        // Opt-in for imported car AND wheel prefabs with valid URP Lit PBR materials.
        // Legacy Kenney models still use the shared atlas and unchanged art budget.
        public bool preserveAuthoredUrpMaterials;
        // Runtime-only: some higher-fidelity FBXs include four separate wheel
        // objects. They are detached and driven by the existing WheelColliders.
        [NonSerialized] public bool useEmbeddedAuthoredWheels;
        // OBJ bodies with verified geometry can keep the existing dedicated
        // WheelCollider visuals while their source materials are converted once
        // to shared URP Lit materials. No per-instance/per-frame materials.
        [NonSerialized] public bool convertAuthoredBodyMaterials;
    }

    [Serializable]
    internal sealed class AarisVehicleTypesFile
    {
        public AarisVehicleType[] vehicles;
    }

    internal sealed class AarisVehicleRecord
    {
        public AarisVehiclePoint point;
        public AarisVehicleType type;
        public Vector3 position;
        public Quaternion rotation;
        public AarisVehicleMotor visible;
        public GameObject builtBody, builtWheels;
        public Bounds builtBounds;
        public float nextProbe;
        public bool previouslySpawned;
        // Stored across a streamed-out vehicle instance, just like position
        // and orientation. A collision must not magically repair after 160m.
        public float damage01;
        // Rotate the one nearby fallback position only after a failed spawn.
        public int failedSpawnProbes;
    }

    /// <summary>
    /// Match-seeded vehicles. The pre-baked 45 garage nodes + reviewed
    /// roadside candidates are deliberately chosen across 500m sectors.
    /// Collider checks occur ONLY close to the player, while the existing
    /// AarisNearbyColliders has activated nearby buildings and bridges.
    /// </summary>
    [DefaultExecutionOrder(-70), DisallowMultipleComponent]
    public sealed class AarisVehicleWorld : MonoBehaviour
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static readonly ProfilerMarker k_VehicleBuild =
            new ProfilerMarker("Aaris.Streaming.VehicleBuild");
        private static readonly ProfilerMarker k_VehicleReuse =
            new ProfilerMarker("Aaris.Streaming.VehicleReuse");
#endif
        // Long enough to reveal parked cars before the player reaches them;
        // the 10-car active cap and 2GB Android spawn budget stay unchanged.
        private const float LoadRadius = 105f;
        private const float AuthoredPrewarmRadius = 150f;
        private const float UnloadRadius = 160f;
        private const float SeaLevel = -38.07552f;
        private const int TargetVehicles = 44;
        private const int MaximumNearbyVehicles = 10;
        private const int MaximumParkedCache = 4;
        private const float ParkedCacheLifetime = 30f;

        private struct ParkedVehicle
        {
            internal AarisVehicleMotor motor;
            internal AarisVehicleType type;
            internal GameObject body, wheels;
            internal Bounds bounds;
            internal float expiresAt;
        }

        private readonly List<ParkedVehicle> parkedCache =
            new List<ParkedVehicle>(MaximumParkedCache);
        internal static AarisVehicleWorld ActiveWorld { get; private set; }
        private int parkedCacheLimit = MaximumParkedCache;
        private float parkedCacheAllowedAt;
        private bool lowMemoryPending;
        internal int FreshVehicleBuilds { get; private set; }
        internal int ReusedVehicleBuilds { get; private set; }
        internal int ParkedCacheCount => parkedCache.Count;
        private readonly List<AarisVehicleRecord> plans = new List<AarisVehicleRecord>(TargetVehicles);
        private readonly RaycastHit[] rayHits = new RaycastHit[24];
        private readonly Collider[] collisionHits = new Collider[40];
        private Transform player;
        private AarisNearbyColliders nearbyColliders;
        // Reuse the four native terrain components across repeated five-ray
        // vehicle support checks; re-query active terrains only on a rare miss.
        private Terrain[] terrainCache;
        private float nextTerrainRefresh;
        private float nextPoll;
#if UNITY_ANDROID && !UNITY_EDITOR
        // Prediction is ONLY a low-priority vehicle FBX prefetch hint.
        // Spawn/unload distance, physical gameplay and original art stay unchanged.
        private const float VehiclePrewarmLeadSeconds = 1f;
        private const float VehiclePrewarmMaxSpeedMetresPerSecond = 32f;
        private const float VehiclePrewarmTeleportSpeedMetresPerSecond = 45f;
        private Vector3 previousPrewarmPosition;
        private float previousPrewarmTime;
        private bool hasPrewarmSample;
#endif
        private uint randomState;
        private AarisVehicleType[] vehicleTypes;
        private GameObject[] prefabs;
        private GameObject[] wheelPrefabs;
        // Keep verified lightweight art available if an imported FBX later
        // turns out to have broken/empty wheel or body geometry on device.
        private GameObject[] safeBodyPrefabs;
        private GameObject[] safeWheelPrefabs;
        // High-detail authored bodies are prewarmed asynchronously only when
        // the player approaches a planned car. This preserves the exact same
        // visuals while avoiding five multi-megabyte FBX loads during boot.
        private ResourceRequest[] authoredBodyRequests;
#if UNITY_ANDROID && !UNITY_EDITOR
        // Keep the completed body request while its authored wheel is loading.
        // This prevents duplicate body prewarms without increasing parked cars.
        private ResourceRequest[] authoredWheelRequests;
        private GameObject[] authoredBodiesAwaitingWheels;
#endif
        private bool[] authoredBodyLoadingFallback;
        private bool[] authoredBodyResolved;
        private int validModels;
        private Material paletteMaterial;
        private Material wheelPaletteMaterial;
        // Scene-shared lamp materials. At most ten streamed cars use these
        // exact instances; never allocate one material for every vehicle.
        private Material headlampGlassMaterial, headlampOnMaterial;
        private Material taillampGlassMaterial, brakeLampMaterial;
        private Material reverseLampMaterial;
        // One chamfered mesh + one dark bezel material for the entire match.
        // Individual car lenses use MeshFilter.sharedMesh rather than toy
        // cubes (with default Colliders) or per-instance mesh allocations.
        private Mesh sharedLampLensMesh;
        private Material lampBezelMaterial;
        // One scene-owned URP mark material shared by both rear trails on
        // every streamed car, instead of allocating one material per wheel.
        private Material skidMarkMaterial;
        // One shared URP particle shader material for occupied-car exhaust;
        // the parked 44-car world plan allocates no particle systems.
        private Material exhaustMaterial;
        // Convert each imported source material once, then share it across all
        // streamed instances. This preserves PBR texture subassets without
        // per-car material duplication on low-memory Android devices.
        private readonly Dictionary<Material, Material> authoredMaterialCache =
            new Dictionary<Material, Material>();
        // Nonalloc native component scans are reused across nearby car spawns.
        private readonly List<Collider> buildColliders = new List<Collider>(48);
        private readonly List<Renderer> buildRenderers = new List<Renderer>(48);
        // All fallback submeshes point to one existing scene-owned URP material.
        private readonly Dictionary<int, Material[]> paletteSlotLayouts =
            new Dictionary<int, Material[]>();
        // Instantiate/Destroy of a complete car can spike a 2 GB Android frame.
        // Bound structural changes per 300 ms poll; retain all 44 authored plans.
        private int spawnBudgetPerPoll = 2;
        private int unloadBudgetPerPoll = 4;
        // Even a failed spawn has up to five ground rays + one box overlap.
        // Limit physics probes as well as successful Instantiate operations.
        private int spawnProbeBudgetPerPoll = 8;
#if UNITY_ANDROID && !UNITY_EDITOR
        // A single rejected parking spot can run several native physics queries.
        // Budget later probes by elapsed CPU wall-clock, not only attempt count.
        // Keep the first attempt per poll so streaming never starves.
        private const double AndroidSpawnProbeSliceSeconds = 0.0015d;
#endif
        // Heavy authored FBXs can each occupy several megabytes once Unity
        // expands mesh/material data. Bound simultaneous async imports so
        // low-RAM phones never stack six vehicle bodies in one memory spike.
        private int maxConcurrentAuthoredLoads = 2;

        // Unity 6 can prepare one immutable vehicle body away from the actual
        // 105 m visibility transition. The clone stays under an inactive root,
        // so this changes neither visible car count nor physics. Full parked-car
        // reuse still wins; this only removes the cold hierarchy clone from the
        // common pool-miss spawn frame on constrained Android devices.
        private const float PreparedBodyLifetimeSeconds = 12f;
        private const float AsyncVehicleIntegrationBudgetMs = 1f;
        private AsyncInstantiateOperation<GameObject> preparedBodyOperation;
        private GameObject preparedBodyClone, preparedBodySource;
        private GameObject preparedBodyStagingRoot;
        private float preparedBodyExpiresAt;
        private float previousAsyncInstantiateIntegrationMs;
        private bool asyncInstantiateBudgetOverridden;
        internal int AsyncPreparedBodyStarts { get; private set; }
        internal int AsyncPreparedBodyConsumes { get; private set; }

        // Bounds in VEHICLE-LOCAL coordinates, not Unity Renderer.bounds (which
        // rotates with road heading). A 90-degree parked sedan must not be
        // interpreted as a sideways FBX and rotated a second time.
        // Evaluated once when a model spawns; never on the frame hot path.
        // Palette-only vehicle paths do not need to deserialize source
        // materials just to discover how many renderer slots exist. Reading
        // Renderer.sharedMaterials can force Unity to hydrate a damaged/stale
        // material reference and has crashed the Linux Editor natively. Mesh
        // topology is the authoritative slot count and is safe to query here.
        internal static int VisualSubmeshCount(Renderer renderer)
        {
            if (renderer == null) return 1;
            if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                return Mathf.Max(1, skinned.sharedMesh.subMeshCount);
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null
                ? Mathf.Max(1, filter.sharedMesh.subMeshCount) : 1;
        }

        // Renderer components alone do not prove the imported car can be seen.
        // Fail safely if its only meshes are disabled, inactive or forced off.
        private static bool HasRenderableVehicleBody(IReadOnlyList<Renderer> renderers)
        {
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled ||
                    renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (renderer is SkinnedMeshRenderer skinned &&
                    skinned.sharedMesh != null && skinned.sharedMesh.vertexCount > 0)
                    return true;
                if (renderer is MeshRenderer)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null &&
                        filter.sharedMesh.vertexCount > 0)
                        return true;
                }
            }
            return false;
        }

        // Imported FBX wheel transforms may have valid bounds while their
        // renderer is disabled, forced off, inactive, or missing real geometry.
        // Validate only once at car creation; never query art per physics frame.
        // Per imported car these queries run for each wheel and the authored
        // material preflight. Reuse results, not one Renderer[] per wheel.
        [ThreadStatic] private static List<Renderer> validationRenderers;
        [ThreadStatic] private static List<Material> validationMaterials;

        internal static bool HasRenderableWheelGeometry(Transform wheelRoot)
        {
            if (wheelRoot == null || !wheelRoot.gameObject.activeInHierarchy)
                return false;
            List<Renderer> renderers = validationRenderers;
            if (renderers == null)
                validationRenderers = renderers = new List<Renderer>(12);
            try
            {
                renderers.Clear();
                wheelRoot.GetComponentsInChildren<Renderer>(true, renderers);
                return HasRenderableVehicleBody(renderers);
            }
            finally
            {
                // Streamed-out wheel meshes must not remain referenced after
                // an early return or failed native renderer query.
                renderers.Clear();
            }
        }

        internal static bool TryGetVisualBoundsLocal(Transform localSpace, out Bounds combined)
        {
            return TryGetVisualBoundsLocal(localSpace, localSpace, out combined);
        }

        // Bounds fitting can execute repeatedly for the same newly imported
        // vehicle and each of its wheels. Unity 6 can fill caller-owned lists
        // instead of allocating two new component arrays on every query.
        // Unity scene APIs run on the main thread, but thread-local storage
        // additionally prevents callers on separate threads sharing buffers.
        [ThreadStatic] private static List<MeshFilter> visualBoundsFilters;
        [ThreadStatic] private static List<SkinnedMeshRenderer> visualBoundsSkins;

        // Measure one imported subtree directly in another transform's axes.
        // Project source mesh bounds once, never a world-aligned bounding box.
        internal static bool TryGetVisualBoundsLocal(Transform localSpace,
            Transform subtree, out Bounds combined)
        {
            combined = new Bounds();
            if (localSpace == null || subtree == null) return false;
            bool found = false;
            Matrix4x4 toLocal = localSpace.worldToLocalMatrix;
            List<MeshFilter> filters = visualBoundsFilters;
            if (filters == null)
                visualBoundsFilters = filters = new List<MeshFilter>(24);
            List<SkinnedMeshRenderer> skins = visualBoundsSkins;
            if (skins == null)
                visualBoundsSkins = skins = new List<SkinnedMeshRenderer>(4);
            try
            {
                filters.Clear();
                subtree.GetComponentsInChildren<MeshFilter>(true, filters);
                foreach (var filter in filters)
                {
                    if (filter == null || filter.sharedMesh == null ||
                        filter.sharedMesh.vertexCount == 0) continue;
                    // Imported FBXs can contain disabled helper/LOD meshes that
                    // are much larger than the visible shell. Fitting collider,
                    // lamps and wheels to those hidden bounds makes cars tiny or
                    // apparently float. Measure only actual rendered geometry.
                    var renderer = filter.GetComponent<MeshRenderer>();
                    if (renderer == null || !renderer.enabled ||
                        renderer.forceRenderingOff ||
                        !renderer.gameObject.activeInHierarchy) continue;
                    AccumulateLocalBounds(filter.sharedMesh.bounds,
                        toLocal * filter.transform.localToWorldMatrix,
                        ref combined, ref found);
                }
                skins.Clear();
                subtree.GetComponentsInChildren<SkinnedMeshRenderer>(true, skins);
                foreach (var skinned in skins)
                {
                    if (skinned == null || skinned.sharedMesh == null ||
                        skinned.sharedMesh.vertexCount == 0 || !skinned.enabled ||
                        skinned.forceRenderingOff ||
                        !skinned.gameObject.activeInHierarchy) continue;
                    AccumulateLocalBounds(skinned.localBounds,
                        toLocal * skinned.transform.localToWorldMatrix,
                        ref combined, ref found);
                }
                return found;
            }
            finally
            {
                // Do not retain retired imported meshes, skins or GameObjects
                // after stream-out, including an exceptional native query.
                filters.Clear();
                skins.Clear();
            }
        }

        private static void AccumulateLocalBounds(Bounds meshBounds, Matrix4x4 toLocal,
            ref Bounds combined, ref bool found)
        {
            Vector3 e = meshBounds.extents;
            Vector3 projected = new Vector3(
                Mathf.Abs(toLocal.m00) * e.x + Mathf.Abs(toLocal.m01) * e.y +
                    Mathf.Abs(toLocal.m02) * e.z,
                Mathf.Abs(toLocal.m10) * e.x + Mathf.Abs(toLocal.m11) * e.y +
                    Mathf.Abs(toLocal.m12) * e.z,
                Mathf.Abs(toLocal.m20) * e.x + Mathf.Abs(toLocal.m21) * e.y +
                    Mathf.Abs(toLocal.m22) * e.z);
            Bounds part = new Bounds(toLocal.MultiplyPoint3x4(meshBounds.center),
                projected * 2f);
            if (!found) { combined = part; found = true; }
            else combined.Encapsulate(part);
        }

        private void Awake()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (AarisMobileStreamingFrameGate.IsConstrainedDevice())
                parkedCacheLimit = 2;
            int memoryMB = SystemInfo.systemMemorySize;
            if ((memoryMB > 0 && memoryMB <= 3072) ||
                AarisMobileRuntime.UseConstrainedGpuStreamingBudget())
            {
                spawnBudgetPerPoll = 1;
                // Native Destroy finalizes after Update; limit low-end GPU/PhysX
                // teardown to one distant parked car per 300 ms poll.
                // No car count, visibility radius or authored asset is reduced.
                unloadBudgetPerPoll = 1;
                spawnProbeBudgetPerPoll = 3;
                maxConcurrentAuthoredLoads = 1;
            }
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetActiveWorld() => ActiveWorld = null;

        private void OnEnable()
        {
            ActiveWorld = this;
            Application.lowMemory += OnLowMemory;
        }

        private void OnDisable()
        {
            if (ActiveWorld == this) ActiveWorld = null;
            Application.lowMemory -= OnLowMemory;
            // A disabled world cannot pump an operation deliberately waiting
            // for main-thread integration. Cancel it rather than retaining
            // hidden native work or a global integration-budget override.
            ReleasePreparedBodyPreparation(false);
        }

        private void OnLowMemory() { lowMemoryPending = true; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            EnsureForPlayer(FindFirstObjectByType<AarisThirdPersonPlayer>());
        }

        internal static void EnsureForPlayer(AarisThirdPersonPlayer motor)
        {
            if (motor == null || FindFirstObjectByType<AarisVehicleWorld>() != null) return;
            var obj = new GameObject("Aaris 2km Match Vehicles (streamed)");
            var world = obj.AddComponent<AarisVehicleWorld>();
            world.player = motor.transform;
#if UNITY_ANDROID && !UNITY_EDITOR
            // On 4 GB Android or a constrained GPU, avoid synchronously
            // hydrating every fallback car and wheel on the first map frame.
            // This changes resource scheduling, not the car/physics/visual plan.
            if ((SystemInfo.systemMemorySize > 0 &&
                 SystemInfo.systemMemorySize <= 4096) ||
                AarisMobileRuntime.UseConstrainedGpuStreamingBudget())
            {
                world.StartCoroutine(world.SetupAfterBudgetAndroidPrewarm(motor));
                return;
            }
#endif
            if (!world.Setup()) { Destroy(obj); return; }
            AttachSessionAndHud(motor, world);
        }

        private static void AttachSessionAndHud(
            AarisThirdPersonPlayer motor, AarisVehicleWorld world)
        {
            if (motor == null || world == null) return;
            var session = motor.GetComponent<AarisVehicleSession>();
            if (session == null) session = motor.gameObject.AddComponent<AarisVehicleSession>();
            session.Configure(world, motor);
            var hudObject = new GameObject("Aaris Four Arrow Drive HUD");
            var hud = hudObject.AddComponent<AarisVehicleHUD>();
            hud.Configure(session);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private IEnumerator SetupAfterBudgetAndroidPrewarm(AarisThirdPersonPlayer motor)
        {
            // The scene should render before catalogue/mesh integrations begin.
            yield return null;
            // The nearby-bike system can request these same road positions
            // during Start(). Join its in-flight load instead of issuing a
            // second native import or later doing a synchronous lookup.
            yield return AarisVehiclePointManifest.LoadAndroidAsync();
            if (motor == null || player == null) { Destroy(gameObject); yield break; }
            TextAsset preloadedPoints = AarisVehiclePointManifest.AndroidCached;
            yield return null; // keep JSON/native request integration off the next import
            ResourceRequest catalogueRequest =
                Resources.LoadAsync<TextAsset>("AarisVehicles/vehicle_catalog");
            catalogueRequest.priority = 0; // gameplay car types, not scenery
            yield return catalogueRequest;

            TextAsset text = catalogueRequest.asset as TextAsset;
            if (text != null)
            {
                AarisVehicleTypesFile definitions = null;
                try { definitions = JsonUtility.FromJson<AarisVehicleTypesFile>(text.text); }
                catch (Exception) { /* Setup() retains authoritative error logging. */ }

                if (definitions != null && definitions.vehicles != null)
                {
                    // One in-flight request, one integration at a time.
                    // Authored high-fidelity FBX prewarm remains proximity-based
                    // and is NEVER added to this startup queue.
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    foreach (AarisVehicleType type in definitions.vehicles)
                    {
                        if (type == null || type.weight <= 0 ||
                            type.maxSpeedMetres < 8f || type.maxSpeedMetres > 28f)
                            continue;
                        string[] paths = { type.modelResource, type.wheelResource };
                        foreach (string path in paths)
                        {
                            if (string.IsNullOrEmpty(path) || !visited.Add(path))
                                continue;
                            ResourceRequest request;
                            while ((request = AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(path)) == null)
                                yield return null;
                            yield return request;
                            // Space out Unity main-thread integration and URP
                            // object initialization, even for tiny fallback kits.
                            yield return null;
                        }
                    }
                }
            }

            if (motor == null || player == null) { Destroy(gameObject); yield break; }
            // Setup() performs synchronous reference/material validation even
            // after async prewarm. Reserve a separate optional integration
            // frame so it cannot overlap house and pickup construction.
            while (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                yield return null;
            // All original validation, authored-asset fallback, car placement,
            // material quality and gameplay rules are still performed here.
            if (!Setup(preloadedPoints, text)) { Destroy(gameObject); yield break; }
            AttachSessionAndHud(motor, this);
        }
#endif

        // Preserve the original immediate Editor/non-constrained route.
        private bool Setup()
        {
            return Setup(
                Resources.Load<TextAsset>("AarisVehicles/vehicle_candidates"),
                Resources.Load<TextAsset>("AarisVehicles/vehicle_catalog"));
        }

        private bool Setup(TextAsset points, TextAsset catalog)
        {
            // On constrained Android both manifests are preloaded, so do NOT
            // re-open them synchronously after long model prewarm.
            nearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();
            if (points == null || catalog == null)
            {
                Debug.LogError("Aaris vehicles: authored offline candidates/catalog are missing.");
                return false;
            }
            AarisVehiclePointsFile file;
            AarisVehicleTypesFile definitions;
            try
            {
                file = JsonUtility.FromJson<AarisVehiclePointsFile>(points.text);
                definitions = JsonUtility.FromJson<AarisVehicleTypesFile>(catalog.text);
            }
            catch (Exception e)
            {
                Debug.LogError("Aaris vehicles: malformed offline data: " + e.Message);
                return false;
            }
            if (file == null || file.schema != 1 || file.sizeMetres != 2000 ||
                file.hubCount != 45 || file.candidates == null ||
                file.candidates.Length < 70 || definitions == null ||
                definitions.vehicles == null || definitions.vehicles.Length < 3)
            {
                Debug.LogError("Aaris vehicles: source data does not match the 2km authored world.");
                return false;
            }
            vehicleTypes = definitions.vehicles;
            prefabs = new GameObject[vehicleTypes.Length];
            wheelPrefabs = new GameObject[vehicleTypes.Length];
            safeBodyPrefabs = new GameObject[vehicleTypes.Length];
            safeWheelPrefabs = new GameObject[vehicleTypes.Length];
            authoredBodyRequests = new ResourceRequest[vehicleTypes.Length];
#if UNITY_ANDROID && !UNITY_EDITOR
            authoredWheelRequests = new ResourceRequest[vehicleTypes.Length];
            authoredBodiesAwaitingWheels = new GameObject[vehicleTypes.Length];
#endif
            authoredBodyLoadingFallback = new bool[vehicleTypes.Length];
            authoredBodyResolved = new bool[vehicleTypes.Length];
            int availableWeight = 0;
            for (int i = 0; i < vehicleTypes.Length; i++)
            {
                var item = vehicleTypes[i];
                if (item == null || item.weight <= 0 || item.maxSpeedMetres < 8f ||
                    item.maxSpeedMetres > 28f) continue;
                prefabs[i] = Resources.Load<GameObject>(item.modelResource);
                wheelPrefabs[i] = Resources.Load<GameObject>(item.wheelResource);
                // Do not load multi-megabyte authored FBXs here. The small
                // legacy body/wheel pair proves the type is spawnable and stays
                // as a fail-safe; authoredBodyResolved is completed by the
                // proximity prewarmer before the first visible spawn.
                authoredBodyResolved[i] =
                    string.IsNullOrEmpty(item.authoredModelResource);
                // A valid GameObject without a usable mesh is not a drivable
                // 3D model. Remove both references so weighted selection
                // cannot plan an invisible car for an entire map sector.
                if (prefabs[i] == null || wheelPrefabs[i] == null ||
                    prefabs[i].GetComponentInChildren<Renderer>(true) == null ||
                    wheelPrefabs[i].GetComponentInChildren<Renderer>(true) == null)
                {
                    prefabs[i] = null;
                    wheelPrefabs[i] = null;
                    continue;
                }

                // A Standard/pink/missing material must never silently ship as
                // a realistic car. Fail this *type* closed, not the full match.
                if (item.preserveAuthoredUrpMaterials &&
                    (!HasCompatibleUrpMaterials(prefabs[i]) ||
                     !HasCompatibleUrpMaterials(wheelPrefabs[i])))
                {
                    Debug.LogWarning("Aaris vehicles: incompatible URP materials on " + item.id);
                    prefabs[i] = null;
                    wheelPrefabs[i] = null;
                    continue;
                }
                safeBodyPrefabs[i] = prefabs[i];
                safeWheelPrefabs[i] = wheelPrefabs[i];
                availableWeight += item.weight;
                validModels++;
            }
            // A single missing imported FBX must not disable every valid
            // vehicle on a low-memory Android device. We still refuse to
            // spawn white primitive placeholders or cars without real wheels.
            if (validModels == 0 || availableWeight <= 0)
            {
                Debug.LogError("Aaris vehicles: no usable local CC0 car and wheel models.");
                return false;
            }
            if (validModels < vehicleTypes.Length)
                Debug.LogWarning("Aaris vehicles: " + validModels + "/" +
                    vehicleTypes.Length + " local vehicle types available.");
            Texture2D palette = Resources.Load<Texture2D>("AarisVehicles/Models/colormap");
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null || palette == null)
            {
                Debug.LogError("Aaris vehicles: verified URP palette/shader missing, refusing white or pink placeholder cars.");
                return false;
            }
            // Legacy Kenney bodies and wheels share the same UV atlas but
            // should not share the same BRDF. Painted sheet metal benefits
            // from tighter reflections; tyres/rims must stay visibly rough.
            // These are two shared materials for the whole match, not per-car
            // instances, so the visual upgrade does not scale allocations with
            // the streamed vehicle count.
            paletteMaterial = new Material(shader) { name = "Aaris CC0 Kenney Car Body" };
            paletteMaterial.SetTexture("_BaseMap", palette);
            paletteMaterial.SetColor("_BaseColor", Color.white);
            // Palette mixes body, windows and trim; do NOT clear-coat the
            // atlas. Better polished response comes from restrained URP Lit
            // metallic/smoothness, with zero extra texture reads or passes.
            paletteMaterial.SetFloat("_Metallic", .24f);
            paletteMaterial.SetFloat("_Smoothness", .60f);
            paletteMaterial.enableInstancing = true;

            wheelPaletteMaterial = new Material(shader) { name = "Aaris CC0 Kenney Wheel Rubber" };
            wheelPaletteMaterial.SetTexture("_BaseMap", palette);
            wheelPaletteMaterial.SetColor("_BaseColor", Color.white);
            wheelPaletteMaterial.SetFloat("_Metallic", 0f);
            wheelPaletteMaterial.SetFloat("_Smoothness", .18f);
            wheelPaletteMaterial.enableInstancing = true;

            headlampGlassMaterial = CreateLampMaterial(shader, "Headlamp Glass",
                new Color(.53f, .63f, .70f), Color.black);
            headlampOnMaterial = CreateLampMaterial(shader, "Active Headlights",
                new Color(1f, .94f, .75f), new Color(1.8f, 1.65f, 1.25f));
            taillampGlassMaterial = CreateLampMaterial(shader, "Rear Red Lenses",
                new Color(.42f, .028f, .024f), Color.black);
            brakeLampMaterial = CreateLampMaterial(shader, "Active Brake Lamps",
                new Color(1f, .065f, .045f), new Color(2.2f, .06f, .025f));
            reverseLampMaterial = CreateLampMaterial(shader, "Reverse Lamps",
                new Color(.90f, .97f, 1f), new Color(1.7f, 1.85f, 2f));
            sharedLampLensMesh = BuildLampLensMesh();
            lampBezelMaterial = CreateLampMaterial(shader, "Lamp Dark Bezels",
                new Color(.035f, .045f, .052f), Color.black);
            skidMarkMaterial = CreateSkidMarkMaterial(shader);
            // Optional visual feature: absent particle shader must NEVER
            // abort otherwise valid drivable cars or cause pink materials.
            Shader smokeShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (smokeShader != null)
                exhaustMaterial = CreateExhaustMaterial(smokeShader);

            // Stable per-match plan. A multiplayer server can later provide
            // this seed to clients; local standalone has no server authority.
            randomState = unchecked((uint)DateTime.UtcNow.Ticks) ^
                unchecked((uint)Environment.TickCount) ^ 0xB4217C35u;
            if (randomState == 0) randomState = 0x89A51C3Bu;
            var candidates = new List<AarisVehiclePoint>();
            foreach (var point in file.candidates)
            {
                if (point == null || Mathf.Abs(point.x) > 950f ||
                    Mathf.Abs(point.z) > 950f) continue;
                candidates.Add(point);
            }
            Shuffle(candidates);
            var sectors = new HashSet<int>();
            // Stage one: avoid the city-first distribution by choosing one
            // candidate in every available 500x500m sector.
            foreach (var point in candidates)
            {
                int sector = Mathf.Clamp(Mathf.FloorToInt((point.x + 1000f) / 500f), 0, 3)
                    + 4 * Mathf.Clamp(Mathf.FloorToInt((point.z + 1000f) / 500f), 0, 3);
                if (sectors.Contains(sector)) continue;
                if (AddPlan(point, 100f)) sectors.Add(sector);
            }
            // Stage two: prefer the 45 real garage spots over arbitrary
            // roadside placements while retaining map-wide coverage.
            candidates.Sort((a, b) =>
                (a.kind == "hub" ? 0 : 1).CompareTo(b.kind == "hub" ? 0 : 1));
            foreach (var point in candidates)
            {
                if (plans.Count >= TargetVehicles) break;
                AddPlan(point, 105f);
            }
            Debug.Log("AARIS_VEHICLE_MATCH planned=" + plans.Count +
                " hubs=45 authoredCandidates=" + file.candidates.Length +
                " sectorCoverage=" + sectors.Count + " (physics validated when nearby)");
            return plans.Count >= 18;
        }

        // Eight-sided flush lenses have chamfered corners rather than the
        // original full-size collision-capable primitive cubes. Six triangles
        // per lens, one shared native mesh for every streamed vehicle.
        private static Mesh BuildLampLensMesh()
        {
            var lens = new Mesh { name = "Aaris Shared Chamfered Vehicle Lens" };
            lens.vertices = new[]
            {
                new Vector3(-.46f, -.50f, 0f),
                new Vector3( .40f, -.50f, 0f),
                new Vector3( .50f, -.34f, 0f),
                new Vector3( .50f,  .31f, 0f),
                new Vector3( .40f,  .50f, 0f),
                new Vector3(-.43f,  .50f, 0f),
                new Vector3(-.50f,  .32f, 0f),
                new Vector3(-.50f, -.35f, 0f)
            };
            lens.triangles = new[]
            {
                0, 1, 2, 0, 2, 3, 0, 3, 4,
                0, 4, 5, 0, 5, 6, 0, 6, 7
            };
            lens.RecalculateNormals();
            lens.RecalculateBounds();
            return lens;
        }

        // URP Lit is already validated at scene startup. Keeping the emission
        // keyword on these shared materials avoids unique per-car GPU copies.
        private static Material CreateLampMaterial(Shader shader, string label,
            Color baseColour, Color emission)
        {
            var lamp = new Material(shader)
            {
                name = "Aaris Vehicle " + label,
                enableInstancing = true
            };
            lamp.SetColor("_BaseColor", baseColour);
            lamp.SetFloat("_Metallic", .05f);
            lamp.SetFloat("_Smoothness", .86f);
            lamp.SetColor("_EmissionColor", emission);
            lamp.EnableKeyword("_EMISSION");
            return lamp;
        }

        // Skid traces live for only a couple of seconds. URP transparent
        // rendering produces thin road-bound strokes, not thick black ribbons.
        // The shared material exists once per scene, never per skid/frame.
        private static Material CreateSkidMarkMaterial(Shader shader)
        {
            var material = new Material(shader)
            {
                name = "Aaris Shared Skid Traces",
                enableInstancing = true
            };
            material.SetColor("_BaseColor",
                new Color(.075f, .073f, .070f, .62f));
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", .12f);
            material.SetOverrideTag("RenderType", "Transparent");
            if (material.HasProperty("_Surface"))
                material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend",
                    (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend",
                    (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue =
                (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return material;
        }

        private static Material CreateExhaustMaterial(Shader shader)
        {
            var material = new Material(shader)
            {
                name = "Aaris Shared Engine Exhaust",
                enableInstancing = true
            };
            material.SetColor("_BaseColor",
                new Color(.60f, .62f, .64f, .44f));
            if (material.HasProperty("_Surface"))
                material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend",
                    (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend",
                    (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue =
                (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return material;
        }

        // The fifth tyre on some SUVs is a tailgate spare, not the bumper.
        // Whole-model bounds can therefore place rear lamps in midair,
        // behind that spare tyre. Use only the named authored body mesh when
        // its bounds are credible; fall back to the fitted full-car shell.
        // Measured once per spawn, never during driving/stream polling.
        private static bool TryGetAuthoredLampBodyBounds(Transform chassis,
            Transform visual, Bounds wholeCar, out Bounds bodyOnly)
        {
            bodyOnly = new Bounds();
            if (chassis == null || visual == null) return false;
            // The model may contain dozens of meshes/LOD helpers. A single
            // caller-owned list also serves the exact wheel-name scans.
            List<Transform> all = namedWheelQueryScratch;
            if (all == null)
                namedWheelQueryScratch = all = new List<Transform>(64);
            try
            {
                all.Clear();
                visual.GetComponentsInChildren<Transform>(true, all);
                foreach (var child in all)
                {
                    if (child == null ||
                        !child.name.EndsWith("_body",
                            StringComparison.OrdinalIgnoreCase) ||
                        child.GetComponent<MeshFilter>() == null) continue;
                    if (!TryGetVisualBoundsLocal(chassis, child, out Bounds candidate))
                        continue;
                    if (candidate.size.x < wholeCar.size.x * .55f ||
                        candidate.size.z < wholeCar.size.z * .68f ||
                        candidate.size.z > wholeCar.size.z * 1.04f ||
                        candidate.size.y < .25f)
                        continue;
                    bodyOnly = candidate;
                    return true;
                }
                return false;
            }
            finally
            {
                // A retired/failed imported car must not be retained by the
                // long-lived shared scratch buffer, even on early success.
                all.Clear();
            }
        }

        // Imported high-detail vehicles resolve four semantic wheel names
        // when facing the model and again when fitting tyre ground contact.
        // Reuse the same Transform list instead of allocating eight arrays.
        [ThreadStatic] private static List<Transform> namedWheelQueryScratch;

        private static Transform FindNamedWheel(Transform root, string token)
        {
            if (root == null) return null;
            List<Transform> all = namedWheelQueryScratch;
            if (all == null)
                namedWheelQueryScratch = all = new List<Transform>(64);
            try
            {
                all.Clear();
                root.GetComponentsInChildren<Transform>(true, all);
                for (int i = 0; i < all.Count; i++)
                    if (all[i] != null &&
                        all[i].name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        return all[i];
                return null;
            }
            finally
            {
                // Never retain a streamed-out FBX hierarchy after the lookup,
                // including early matches and unexpected native query errors.
                all.Clear();
            }
        }

        // Validate only imported high-fidelity art. The legacy fallback
        // cars have their own verified proportions and should never be
        // rejected by this optional visual-quality envelope.
        internal static bool HasPlausibleAuthoredSilhouette(
            Vector3 size, float targetWidth, float targetLength)
        {
            if (float.IsNaN(size.sqrMagnitude) ||
                float.IsInfinity(size.sqrMagnitude) ||
                targetWidth <= 0f || targetLength <= 0f) return false;
            // Height limits admit the low GT coupe and tall utility/van.
            // A substantial fraction of each class's 7/8m-road footprint
            // must be visible, not squeezed into a toy-sized body.
            return size.x >= targetWidth * .74f &&
                size.x <= targetWidth * 1.08f &&
                size.z >= targetLength * .78f &&
                size.z <= targetLength * 1.08f &&
                size.y >= .82f && size.y <= 2.58f &&
                size.x >= size.z * .28f &&
                size.x <= size.z * .72f;
        }

        // FBX exporters disagree about forward axes. The higher-fidelity
        // vehicles have semantic wheel names, so infer their true nose from
        // front-vs-rear axle centers after the broad X/Z bounds correction.
        // This is one spawn-time hierarchy pass and never touches physics.
        private static void FaceEmbeddedVehicleForward(Transform chassis,
            Transform model)
        {
            if (chassis == null || model == null) return;
            Transform fl = FindNamedWheel(model, "wheel_fl");
            Transform fr = FindNamedWheel(model, "wheel_fr");
            Transform rl = FindNamedWheel(model, "wheel_rl");
            Transform rr = FindNamedWheel(model, "wheel_rr");
            if (fl == null || fr == null || rl == null || rr == null) return;
            float frontZ = (chassis.InverseTransformPoint(fl.position).z +
                            chassis.InverseTransformPoint(fr.position).z) * .5f;
            float rearZ = (chassis.InverseTransformPoint(rl.position).z +
                           chassis.InverseTransformPoint(rr.position).z) * .5f;
            if (Mathf.Abs(frontZ - rearZ) < .20f) return;
            if (frontZ < rearZ)
                model.localRotation *= Quaternion.Euler(0f, 180f, 0f);
        }

        // Imported bodies are authored around different origins. Once the
        // whole model is uniformly scaled, align the actual FOUR tyre bottoms
        // to a common world-ground reference at the authored road support.
        // Static prefab/spawn-time only: no raycasts, frame allocations or
        // runtime correction of vehicle Rigidbody velocity.
        private static bool AlignEmbeddedTyresToRoad(Transform chassis,
            Transform model)
        {
            if (chassis == null || model == null) return false;
            string[] names = { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" };
            float totalBottom = 0f;
            float minimumBottom = float.PositiveInfinity;
            float maximumBottom = float.NegativeInfinity;
            for (int i = 0; i < names.Length; i++)
            {
                Transform tyre = FindNamedWheel(model, names[i]);
                if (tyre == null || !TryGetVisualBoundsLocal(
                    chassis, tyre, out Bounds tyreBounds)) return false;
                float diameter = Mathf.Max(tyreBounds.size.y, tyreBounds.size.z);
                if (diameter < .40f || diameter > 1.06f) return false;
                float bottom = tyreBounds.min.y;
                if (float.IsNaN(bottom) || float.IsInfinity(bottom)) return false;
                totalBottom += bottom;
                minimumBottom = Mathf.Min(minimumBottom, bottom);
                maximumBottom = Mathf.Max(maximumBottom, bottom);
            }
            // Different wheel heights indicate a tilted/broken source rig,
            // not a value to correct by blindly moving the whole car.
            if (maximumBottom - minimumBottom > .22f) return false;
            // Spawn support raises the chassis root .71 m above the ground.
            // The tyres should start with a slight 10 cm spring-compression
            // margin, not float 60+ cm above roads before PhysX settles.
            const float TyreBottomInChassis = -.71f + .10f;
            float offset = TyreBottomInChassis - totalBottom * .25f;
            if (Mathf.Abs(offset) > .95f) return false;
            model.localPosition += Vector3.up * offset;
            return true;
        }

        private static bool HasEmbeddedWheelSet(GameObject source)
        {
            if (source == null) return false;
            bool fl = false, fr = false, rl = false, rr = false;
            List<Transform> all = namedWheelQueryScratch;
            if (all == null)
                namedWheelQueryScratch = all = new List<Transform>(64);
            try
            {
                all.Clear();
                source.GetComponentsInChildren<Transform>(true, all);
                foreach (var t in all)
                {
                    if (t == null) continue;
                    // The original wheel tokens are ASCII: compare names in
                    // place instead of allocating one lowercased copy per node.
                    string label = t.name;
                    fl |= label.IndexOf("wheel_fl",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    fr |= label.IndexOf("wheel_fr",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    rl |= label.IndexOf("wheel_rl",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    rr |= label.IndexOf("wheel_rr",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    if (fl && fr && rl && rr) return true;
                }
                return false;
            }
            finally
            {
                all.Clear();
            }
        }

        private Material AuthoredUrpMaterial(Material source)
        {
            if (source == null) return paletteMaterial;
            if (authoredMaterialCache.TryGetValue(source, out Material cached) &&
                cached != null) return cached;
            // Most vendor FBXs already import valid URP Lit PBR textures.
            // Their separate paint/glazing material slots can be polished,
            // but never change an asset or clear-coat a mixed tyre/paint
            // atlas. One source material gets at most one scene-owned copy.
            if (source.shader != null &&
                source.shader.name == "Universal Render Pipeline/Lit")
            {
                string sourceLabel = source.name.ToLowerInvariant();
                bool dedicatedPaint = sourceLabel.Contains("aarispaint") ||
                    sourceLabel.Contains("aarisciviliansurface");
                bool mixedBodyAtlas = sourceLabel.Contains("body_pbr");
                bool separateGlass = sourceLabel.Contains("aarisglass") ||
                    sourceLabel.Contains("window") ||
                    sourceLabel.Contains("_glass") ||
                    sourceLabel.EndsWith("glass", StringComparison.Ordinal);
                if (!dedicatedPaint && !mixedBodyAtlas && !separateGlass)
                    return source;
                var polished = new Material(source)
                {
                    name = "Aaris Automotive " + source.name,
                    enableInstancing = true
                };
                if (mixedBodyAtlas && polished.HasProperty("_Smoothness"))
                {
                    // An opaque baked body texture also contains lights and
                    // windows. Capping its overly mirror-like smoothness
                    // avoids a chrome/toy look without repainting those UVs.
                    polished.SetFloat("_Smoothness", Mathf.Clamp(
                        source.GetFloat("_Smoothness"), .50f, .71f));
                }
                else if (dedicatedPaint)
                {
                    if (polished.HasProperty("_Metallic"))
                        polished.SetFloat("_Metallic", .36f);
                    if (polished.HasProperty("_Smoothness"))
                        polished.SetFloat("_Smoothness", .66f);
                    if (polished.HasProperty("_ClearCoatMask"))
                    {
                        polished.SetFloat("_ClearCoatMask", .32f);
                        if (polished.HasProperty("_ClearCoatSmoothness"))
                            polished.SetFloat("_ClearCoatSmoothness", .76f);
                        polished.EnableKeyword("_CLEARCOAT");
                    }
                }
                else if (separateGlass)
                {
                    // Remain in the original opaque/transparent render mode.
                    // Never switch to transparency: extra sorting/overdraw
                    // hurts 2 GB Android phones, and some models lack an
                    // interior that could be seen through the window.
                    if (polished.HasProperty("_BaseColor"))
                    {
                        Color original = source.GetColor("_BaseColor");
                        Color tint = new Color(.20f, .29f, .38f, original.a);
                        polished.SetColor("_BaseColor",
                            Color.Lerp(original, tint, .24f));
                    }
                    if (polished.HasProperty("_Metallic"))
                        polished.SetFloat("_Metallic", .05f);
                    if (polished.HasProperty("_Smoothness"))
                        polished.SetFloat("_Smoothness", .83f);
                }
                authoredMaterialCache[source] = polished;
                return polished;
            }
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return paletteMaterial;
            var result = new Material(shader)
            {
                name = "Aaris Authored URP " + source.name,
                enableInstancing = true
            };
            Color baseColor = Color.white;
            if (source.HasProperty("_BaseColor"))
                baseColor = source.GetColor("_BaseColor");
            else if (source.HasProperty("_Color"))
                baseColor = source.GetColor("_Color");
            if (result.HasProperty("_BaseColor")) result.SetColor("_BaseColor", baseColor);
            string baseProperty = source.HasProperty("_BaseMap") &&
                source.GetTexture("_BaseMap") != null ? "_BaseMap" : "_MainTex";
            AarisVehicleSurfaceMaps.Transfer(source, result, baseProperty, "_BaseMap");

            if (AarisVehicleSurfaceMaps.Transfer(source, result, "_BumpMap",
                "_BumpMap") != null)
            {
                result.EnableKeyword("_NORMALMAP");
                AarisVehicleSurfaceMaps.TransferStrength(source, result,
                    "_BumpScale", 1f, 2f);
            }
            if (AarisVehicleSurfaceMaps.Transfer(source, result, "_MetallicGlossMap",
                "_MetallicGlossMap") != null)
                result.EnableKeyword("_METALLICSPECGLOSSMAP");
            if (AarisVehicleSurfaceMaps.Transfer(source, result, "_OcclusionMap",
                "_OcclusionMap") != null)
            {
                result.EnableKeyword("_OCCLUSIONMAP");
                AarisVehicleSurfaceMaps.TransferStrength(source, result,
                    "_OcclusionStrength");
            }
            float metallic = source.HasProperty("_Metallic")
                ? source.GetFloat("_Metallic") : .12f;
            float smoothness = source.HasProperty("_Smoothness")
                ? source.GetFloat("_Smoothness") :
                source.HasProperty("_Glossiness")
                    ? source.GetFloat("_Glossiness") : .38f;
            if (result.HasProperty("_Metallic"))
                result.SetFloat("_Metallic", Mathf.Clamp01(metallic));
            if (result.HasProperty("_Smoothness"))
                result.SetFloat("_Smoothness", Mathf.Clamp01(smoothness));

            string label = source.name.ToLowerInvariant();
            bool glass = label.Contains("glass") || label.Contains("window");
            bool darkTrim = label.Contains("dark") || label.Contains("black") ||
                label.Contains("grey") || label.Contains("tire") ||
                label.Contains("tyre") || label.Contains("rubber") ||
                label.Contains("seat") || label.Contains("interior");
            bool bareMetal = label.Contains("aarismetal") ||
                label.Contains("chrome") || label.Contains("alloy");
            bool headlight = label.Contains("headlight");
            bool taillight = label.Contains("taillight") ||
                label.Contains("tail_light");
            if (glass)
            {
                // Legacy imported OBJ glass lacks a modelled cabin. Drawing
                // its panes semi-transparent adds sorting/overdraw on 2GB
                // phones and makes the unbuilt interior look like a hole.
                // Retain a glazed, tinted URP surface but keep it opaque.
                // Imported true PBR URP car glass uses the separate pass above.
                Color smoked = Color.Lerp(
                    new Color(.11f, .17f, .23f, 1f), baseColor, .18f);
                smoked.a = 1f;
                if (result.HasProperty("_BaseColor"))
                    result.SetColor("_BaseColor", smoked);
                if (result.HasProperty("_Metallic")) result.SetFloat("_Metallic", .05f);
                if (result.HasProperty("_Smoothness")) result.SetFloat("_Smoothness", .81f);
                result.SetOverrideTag("RenderType", "Opaque");
                if (result.HasProperty("_Surface")) result.SetFloat("_Surface", 0f);
                if (result.HasProperty("_SrcBlend"))
                    result.SetFloat("_SrcBlend",
                        (float)UnityEngine.Rendering.BlendMode.One);
                if (result.HasProperty("_DstBlend"))
                    result.SetFloat("_DstBlend",
                        (float)UnityEngine.Rendering.BlendMode.Zero);
                if (result.HasProperty("_ZWrite")) result.SetFloat("_ZWrite", 1f);
                result.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                result.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
            }
            else if (headlight || taillight)
            {
                if (result.HasProperty("_Metallic")) result.SetFloat("_Metallic", .04f);
                if (result.HasProperty("_Smoothness")) result.SetFloat("_Smoothness", .68f);
                if (result.HasProperty("_EmissionColor"))
                {
                    Color glow = headlight
                        ? new Color(1.1f, 1.05f, .82f, 1f)
                        : new Color(.72f, .018f, .01f, 1f);
                    result.SetColor("_EmissionColor", glow);
                    result.EnableKeyword("_EMISSION");
                }
            }
            else if (darkTrim)
            {
                if (result.HasProperty("_Metallic")) result.SetFloat("_Metallic", .06f);
                if (result.HasProperty("_Smoothness")) result.SetFloat("_Smoothness", .20f);
            }
            else if (bareMetal)
            {
                // Exposed alloy is a separate PBR material from matte rubber.
                if (result.HasProperty("_Metallic")) result.SetFloat("_Metallic", .66f);
                if (result.HasProperty("_Smoothness")) result.SetFloat("_Smoothness", .52f);
            }
            else
            {
                // Painted steel: enough reflection to read body curvature but
                // not the mirror-like toy finish produced by smoothness=1.
                if (result.HasProperty("_Metallic")) result.SetFloat("_Metallic", .32f);
                if (result.HasProperty("_Smoothness")) result.SetFloat("_Smoothness", .58f);
                // Clear coat only on single-purpose painted panels;
                // never on shared atlases, tyres, lamps or glass.
                if (label.Contains("aarispaint") &&
                    result.HasProperty("_ClearCoatMask"))
                {
                    result.SetFloat("_ClearCoatMask", .34f);
                    if (result.HasProperty("_ClearCoatSmoothness"))
                        result.SetFloat("_ClearCoatSmoothness", .74f);
                    result.EnableKeyword("_CLEARCOAT");
                }
            }
            authoredMaterialCache[source] = result;
            return result;
        }

        private void ApplyAuthoredMaterials(IReadOnlyList<Renderer> renderers)
        {
            for (int r = 0; r < renderers.Count; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null) continue;
                Material[] source = renderer.sharedMaterials;
                if (source == null || source.Length == 0) continue;
                Material[] converted = new Material[source.Length];
                for (int i = 0; i < source.Length; i++)
                    converted[i] = AuthoredUrpMaterial(source[i]);
                renderer.sharedMaterials = converted;
            }
        }

        // One import-time check per model, never a frame-loop allocation.
        // Source FBXs need explicit URP Lit material authoring in the Editor.
        private static bool HasCompatibleUrpMaterials(GameObject source)
        {
            if (source == null) return false;
            List<Renderer> renderers = validationRenderers;
            if (renderers == null)
                validationRenderers = renderers = new List<Renderer>(12);
            List<Material> materials = validationMaterials;
            if (materials == null)
                validationMaterials = materials = new List<Material>(8);
            try
            {
                renderers.Clear();
                source.GetComponentsInChildren<Renderer>(true, renderers);
                if (renderers.Count == 0) return false;
                foreach (var renderer in renderers)
                {
                    if (renderer == null) return false;
                    materials.Clear();
                    // Unity's GetSharedMaterials(List<Material>) reads the
                    // exact original shared slots without a new Material[].
                    renderer.GetSharedMaterials(materials);
                    if (materials.Count == 0) return false;
                    foreach (var material in materials)
                    {
                        if (material == null || material.shader == null ||
                            material.shader.name != "Universal Render Pipeline/Lit" ||
                            !material.HasProperty("_BaseColor") ||
                            !material.HasProperty("_Metallic") ||
                            !material.HasProperty("_Smoothness")) return false;
                    }
                }
                return true;
            }
            finally
            {
                materials.Clear();
                renderers.Clear();
            }
        }

        private int TypeIndex(AarisVehicleType type)
        {
            if (type == null || vehicleTypes == null) return -1;
            for (int i = 0; i < vehicleTypes.Length; i++)
                if (ReferenceEquals(vehicleTypes[i], type)) return i;
            return -1;
        }

        private void CommitAuthoredBody(int i, GameObject authoredBody, GameObject loadedWheel = null)
        {
            var item = vehicleTypes[i];
            if (authoredBody == null ||
                authoredBody.GetComponentInChildren<Renderer>(true) == null)
                return;
            if (HasEmbeddedWheelSet(authoredBody))
            {
                prefabs[i] = authoredBody;
                wheelPrefabs[i] = authoredBody;
                item.useEmbeddedAuthoredWheels = true;
                item.preserveAuthoredUrpMaterials = false;
                item.convertAuthoredBodyMaterials = false;
                return;
            }
            if (!string.IsNullOrEmpty(item.authoredWheelResource))
            {
                // The Android prewarmer integrates this separate mesh on a
                // DIFFERENT admitted frame than the authored vehicle body.
                // Editor behaviour remains unchanged for synchronous previews.
#if UNITY_ANDROID && !UNITY_EDITOR
                GameObject authoredWheel = loadedWheel;
#else
                GameObject authoredWheel =
                    Resources.Load<GameObject>(item.authoredWheelResource);
#endif
                if (HasCompatibleUrpMaterials(authoredBody) &&
                    HasCompatibleUrpMaterials(authoredWheel))
                {
                    prefabs[i] = authoredBody;
                    wheelPrefabs[i] = authoredWheel;
                    item.preserveAuthoredUrpMaterials = true;
                    item.useEmbeddedAuthoredWheels = false;
                    item.convertAuthoredBodyMaterials = false;
                }
                return;
            }
            prefabs[i] = authoredBody;
            item.convertAuthoredBodyMaterials = true;
            item.useEmbeddedAuthoredWheels = false;
            item.preserveAuthoredUrpMaterials = false;
        }

        private void PumpAuthoredBodyLoads()
        {
            if (authoredBodyRequests == null) return;
            for (int i = 0; i < authoredBodyRequests.Length; i++)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // An authored sedan wheel must never synchronously import on
                // the exact frame that binds its multi-mesh body. Retain the
                // old lightweight body/wheel until both have been validated.
                if (authoredBodiesAwaitingWheels[i] != null)
                {
                    ResourceRequest wheelRequest = authoredWheelRequests[i];
                    if (wheelRequest == null)
                    {
                        authoredWheelRequests[i] =
                            AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(
                                vehicleTypes[i].authoredWheelResource);
                        continue; // admission rejected or async load just began
                    }
                    if (!wheelRequest.isDone ||
                        !AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                        continue;
                    CommitAuthoredBody(i, authoredBodiesAwaitingWheels[i],
                        wheelRequest.asset as GameObject);
                    authoredWheelRequests[i] = null;
                    authoredBodiesAwaitingWheels[i] = null;
                    authoredBodyRequests[i] = null;
                    authoredBodyResolved[i] = true;
                    continue;
                }
#endif
                var request = authoredBodyRequests[i];
                if (request == null || !request.isDone) continue;
                var body = request.asset as GameObject;
                if (body != null &&
                    body.GetComponentInChildren<Renderer>(true) != null)
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    // A finished async import can still spike renderer/material
                    // binding. Defer its optional native integration to a free frame.
                    if (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                        continue;
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
                    // Body integration owns this frame's shared native slot.
                    // Hold a finished request (and its asset reference) so the
                    // prefetch chooser cannot start the same body twice.
                    if (!string.IsNullOrEmpty(vehicleTypes[i].authoredWheelResource) &&
                        !HasEmbeddedWheelSet(body))
                    {
                        authoredBodiesAwaitingWheels[i] = body;
                        continue;
                    }
#endif
                    authoredBodyRequests[i] = null;
                    CommitAuthoredBody(i, body);
                    authoredBodyResolved[i] = true;
                    continue;
                }
                var item = vehicleTypes[i];
                if (!authoredBodyLoadingFallback[i] &&
                    !string.IsNullOrEmpty(item.fallbackAuthoredModelResource))
                {
                    // Keep the first request until the fallback is admitted.
                    // Otherwise a busy frame would silently lose this vehicle.
                    var fallback = AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(
                        item.fallbackAuthoredModelResource);
                    if (fallback == null) continue;
                    authoredBodyLoadingFallback[i] = true;
                    authoredBodyRequests[i] = fallback;
                }
                else
                {
                    authoredBodyRequests[i] = null;
                    authoredBodyResolved[i] = true; // keep legacy fail-safe
                }
            }
        }

        private bool StartAuthoredBodyPrewarm(int i)
        {
            if (i < 0 || i >= vehicleTypes.Length ||
                authoredBodyResolved[i] || authoredBodyRequests[i] != null)
                return false;
            int inFlight = 0;
            for (int j = 0; j < authoredBodyRequests.Length; j++)
            {
                ResourceRequest pendingRequest = authoredBodyRequests[j];
                if (pendingRequest != null && !pendingRequest.isDone) inFlight++;
#if UNITY_ANDROID && !UNITY_EDITOR
                // A wheel request is a real native import too: keep the same
                // two-request peak-memory cap across bodies AND wheels.
                // A completed high-detail body waiting for its wheel is
                // still resident native memory. Count it as ONE admission,
                // even while its wheel request is in flight. This prevents
                // prefetch from filling every slot with other huge bodies.
                ResourceRequest pendingWheel = authoredWheelRequests[j];
                if (authoredBodiesAwaitingWheels[j] != null) inFlight++;
                else if (pendingWheel != null && !pendingWheel.isDone) inFlight++;
#endif
            }
            if (inFlight >= maxConcurrentAuthoredLoads) return false;
            var item = vehicleTypes[i];
            if (string.IsNullOrEmpty(item.authoredModelResource))
            {
                authoredBodyResolved[i] = true;
                return false;
            }
            var request = AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(
                item.authoredModelResource);
            if (request == null) return false;
            authoredBodyRequests[i] = request;
            authoredBodyLoadingFallback[i] = false;
            return true;
        }

        private uint Next()
        {
            randomState ^= randomState << 13;
            randomState ^= randomState >> 17;
            randomState ^= randomState << 5;
            return randomState;
        }

        private void Shuffle(List<AarisVehiclePoint> candidates)
        {
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = (int)(Next() % (uint)(i + 1));
                var t = candidates[i];
                candidates[i] = candidates[j];
                candidates[j] = t;
            }
        }

        private bool AddPlan(AarisVehiclePoint point, float minimumSeparation)
        {
            foreach (var prior in plans)
            {
                float dx = prior.point.x - point.x, dz = prior.point.z - point.z;
                if (dx * dx + dz * dz < minimumSeparation * minimumSeparation)
                    return false;
            }
            int total = 0;
            for (int i = 0; i < vehicleTypes.Length; i++)
                if (prefabs[i] != null && wheelPrefabs[i] != null)
                    total += vehicleTypes[i].weight;
            // No remainder by zero even if future catalog validation changes.
            if (total <= 0) return false;
            int pick = (int)(Next() % (uint)total), which = -1;
            for (int i = 0; i < vehicleTypes.Length; i++)
            {
                if (prefabs[i] == null || wheelPrefabs[i] == null) continue;
                pick -= vehicleTypes[i].weight;
                if (pick < 0) { which = i; break; }
            }
            if (which < 0) return false;
            plans.Add(new AarisVehicleRecord
            {
                point = point,
                type = vehicleTypes[which],
                position = new Vector3(point.x, point.y, point.z),
                rotation = Quaternion.Euler(0f, point.yaw, 0f)
            });
            return true;
        }

        private static Terrain FindTerrain(Terrain[] terrains, float x, float z)
        {
            if (terrains == null) return null;
            foreach (var terrain in terrains)
            {
                if (terrain == null || !terrain.isActiveAndEnabled ||
                    terrain.terrainData == null) continue;
                var p = terrain.transform.position;
                var size = terrain.terrainData.size;
                if (x >= p.x && x <= p.x + size.x &&
                    z >= p.z && z <= p.z + size.z) return terrain;
            }
            return null;
        }

        private Terrain TerrainAt(float x, float z)
        {
            if (terrainCache == null) terrainCache = Terrain.activeTerrains;
            var terrain = FindTerrain(terrainCache, x, z);
            if (terrain != null || Time.unscaledTime < nextTerrainRefresh)
                return terrain;
            // Detect late-loaded terrain only on a cache miss, never per ray.
            nextTerrainRefresh = Time.unscaledTime + 5f;
            terrainCache = Terrain.activeTerrains;
            return FindTerrain(terrainCache, x, z);
        }

        private bool HasSupport(Vector3 center, float expected, out float y)
        {
            y = 0f;
            var terrain = TerrainAt(center.x, center.z);
            if (terrain == null) return false;
            Vector3 rayOrigin = new Vector3(center.x, expected + 3f, center.z);
            Ray supportRay = new Ray(rayOrigin, Vector3.down);
            int hits = Physics.RaycastNonAlloc(supportRay, rayHits, 5f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            RaycastHit[] supportHits = rayHits;
            if (hits >= rayHits.Length)
            {
                // A full NonAlloc buffer is unordered and may omit the
                // closest drivable floor behind car/house colliders.
                // Preserve the same surface-height and slope validation.
                supportHits = Physics.RaycastAll(supportRay, 5f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                hits = supportHits.Length;
            }
            float bestError = 1.05f;
            bool found = false;
            for (int i = 0; i < hits; i++)
            {
                var h = supportHits[i];
                if (h.collider == null || h.normal.y < .9781f) continue; // <=12 deg
                if (player != null && h.collider.transform.IsChildOf(player)) continue;
                float error = Mathf.Abs(expected - h.point.y);
                if (error >= bestError || h.point.y < SeaLevel + 1.0f) continue;
                bestError = error;
                found = true;
                y = h.point.y;
            }
            return found;
        }

        private bool TryResolve(AarisVehicleRecord record, out Vector3 spot,
            float offsetX = 0f, float offsetZ = 0f)
        {
            spot = default(Vector3);
            var p = record.point;
            // A driven vehicle can be parked miles away from its original
            // marker. Never teleport it back when its physics mesh streams in.
            float x = (record.previouslySpawned ? record.position.x : p.x) + offsetX;
            float z = (record.previouslySpawned ? record.position.z : p.z) + offsetZ;
            var terrain = TerrainAt(x, z);
            if (terrain == null) return false;
            // Terrain.SampleHeight returns a height relative to the Terrain
            // transform, not an absolute Y. Our four native tiles start
            // at -120m: without this correction all authored vehicles fail
            // their +/-2.5m road-height validation before reaching physics.
            float ground = terrain.transform.position.y +
                terrain.SampleHeight(new Vector3(x, 0f, z));
            float authoredY = record.previouslySpawned ? record.position.y - .71f : p.y;
            // Any fallback stays near the original authored road elevation.
            // At an offset, prefer local terrain as the down-ray floor target,
            // without ever trusting a distant rooftop or tall slope.
            // The terrain-altitude gate applies only to never-driven authored nodes.
            // A parked car on a real bridge/raised road can be far above terrain;
            // its saved position must instead pass the support-ray/corner checks below.
            if (!record.previouslySpawned && Mathf.Abs(ground - authoredY) > 2.5f) return false;
            float expectedY = (offsetX != 0f || offsetZ != 0f) ? ground : authoredY;
            if (!HasSupport(new Vector3(x, 0f, z), expectedY, out float y))
                return false;
            Quaternion angle = record.rotation;
            // Use the identical real-metre envelope as visual model fitting.
            // A 5.42 m pickup must check rear-axle support and wall clearance
            // outside a 4.28 m sedan's parking footprint.
            float widthMetres = Mathf.Clamp(
                record.type.referenceWidthMetres > 0f
                    ? record.type.referenceWidthMetres : 1.88f, 1.65f, 2.15f);
            float lengthMetres = Mathf.Clamp(
                record.type.referenceLengthMetres > 0f
                    ? record.type.referenceLengthMetres : 4.28f, 3.35f, 5.50f);
            var forward = angle * Vector3.forward *
                Mathf.Max(1.42f, lengthMetres * .305f);
            var right = angle * Vector3.right *
                Mathf.Max(.79f, widthMetres * .405f);
            // Avoid allocating a new four-corner array on every streamed
            // car retry. This hot path is polled repeatedly on low-RAM phones.
            for (int corner = 0; corner < 4; corner++)
            {
                Vector3 delta = (corner < 2 ? forward : -forward) +
                    ((corner & 1) == 0 ? right : -right);
                var check = new Vector3(x + delta.x, 0f, z + delta.z);
                if (!HasSupport(check, y, out float localY) ||
                    Mathf.Abs(localY - y) > .37f) return false;
            }
            // Clearance is above the supporting ground so TerrainCollider
            // will not be counted as a vehicle-sized wall.
            Vector3 center = new Vector3(x, y + 1.15f, z);
            bool tallBody = record.type.id == "SUV" ||
                record.type.id == "Van" || record.type.id == "Utility";
            Vector3 halfExtents = new Vector3(
                Mathf.Max(1.14f, widthMetres * .55f),
                tallBody ? .94f : .64f,
                Mathf.Max(2.35f, lengthMetres * .55f));
            // Remote 105 m car previews must not ignore house/wall colliders
            // streamed out beyond the player's 55 m activation radius.
            // Defer uncertain locations until their authored shapes are live;
            // never globally enable distant colliders or inflate the mobile budget.
            if (nearbyColliders != null &&
                nearbyColliders.HasPotentialInactiveObstacle(center, halfExtents, angle))
                return false;
            int count = Physics.OverlapBoxNonAlloc(center, halfExtents,
                collisionHits, angle, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            Collider[] obstacles = collisionHits;
            if (count >= collisionHits.Length)
            {
                // Only on saturation: inspect the FULL collider set.
                // Ignoring any unseen wall here would place a car inside
                // a house; automatically failing also rejects safe lots
                // surrounded by harmless ground/terrain colliders.
                obstacles = Physics.OverlapBox(center, halfExtents, angle,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                count = obstacles.Length;
            }
            for (int i = 0; i < count; i++)
            {
                var obstacle = obstacles[i];
                if (obstacle == null || obstacle is TerrainCollider) continue;
                if (player != null && obstacle.transform.IsChildOf(player)) return false;
                // Roads can be a valid floor, but anything occupying the
                // full chassis volume is an unsafe parking location.
                return false;
            }
            spot = new Vector3(x, y + .71f, z);
            return true;
        }

        // Original garage/roadside nodes can be blocked by later building
        // edits. Retain the full five-ray support and inactive-building checks,
        // but try one small adjacent parking location on each later poll.
        // Previously driven cars NEVER shift from their saved parking place.
        private bool TryResolvePlanned(AarisVehicleRecord record, out Vector3 spot)
        {
            if (TryResolve(record, out spot)) return true;
            if (record.previouslySpawned) return false;
            int direction = record.failedSpawnProbes++ & 3;
            Vector3 axis = record.rotation *
                (direction < 2 ? Vector3.right : Vector3.forward);
            float offset = direction % 2 == 0 ? 5.5f : -5.5f;
            return TryResolve(record, out spot, axis.x * offset, axis.z * offset);
        }

        // Imported PBR textures also contain decals/glazing, so strong global
        // recolouring would turn windows and lights into painted plastic. Give
        // dedicated paint slots a full tint and body atlases only a subtle tint.
        // Never recolour the Kenney UV palette or the shared material assets.
        // Premium paint stays readable in low-end Android lighting.
        // No neon, candy hues or flat near-black cars.
        // The deep blue, olive-bronze and teal shades are separated from
        // neutral graphite by at least 0.25 in normalized RGB distance.
        // Muted automotive colours stay legible in the same flat daylight
        // rather than collapsing into four nearly identical grey-blue cars.
        private static readonly Color[] BodyPaintColours =
        {
            new Color(.88f, .87f, .83f, 1f), // ceramic pearl
            new Color(.66f, .70f, .74f, 1f), // satin titanium
            new Color(.31f, .36f, .42f, 1f), // graphite blue steel
            new Color(.55f, .14f, .17f, 1f), // deep burgundy
            new Color(.12f, .20f, .47f, 1f), // navy sapphire (midnight finish)
            new Color(.49f, .48f, .25f, 1f), // muted olive (bronze finish)
            new Color(.72f, .63f, .48f, 1f), // antique champagne
            new Color(.11f, .48f, .53f, 1f)  // dark petrol teal (deep marine finish)
        };

        private static int StablePaintVariant(string pointId, string vehicleId)
        {
            // String.GetHashCode is not a persistence key. FNV-1a produces the
            // same colour after streaming out/in and across game launches.
            unchecked
            {
                uint hash = 2166136261u;
                string key = (pointId ?? string.Empty) + "|" +
                    (vehicleId ?? string.Empty);
                for (int i = 0; i < key.Length; i++)
                    hash = (hash ^ key[i]) * 16777619u;
                return (int)(hash % (uint)BodyPaintColours.Length);
            }
        }

        // Identical on spawn and after damage: one verified paint formula.
        // Quaternius AarisPaint imports as a dark red solid material. Modulating
        // that hue by a blue/silver paint made it look almost black. Dedicated
        // untextured body paint must REPLACE the swatch, not multiply it.
        // Baked atlas textures also contain lamps/windows, so keep those toned.
        internal static Color SelectPaintTint(Material material, Color paint,
            bool bodyAtlas, bool civilianPaint)
        {
            Color original = material.GetColor("_BaseColor");
            if (!bodyAtlas && !civilianPaint)
            {
                Texture baseMap = material.HasProperty("_BaseMap")
                    ? material.GetTexture("_BaseMap") : null;
                Color dedicated = baseMap == null ||
                    baseMap == Texture2D.whiteTexture
                    ? paint : Color.Lerp(original, paint, .85f);
                return new Color(dedicated.r, dedicated.g, dedicated.b, original.a);
            }
            Color paintFactor = bodyAtlas
                ? Color.Lerp(Color.white, paint, .30f)
                : Color.Lerp(Color.white, paint, .45f);
            return new Color(original.r * paintFactor.r,
                original.g * paintFactor.g, original.b * paintFactor.b, original.a);
        }

        private static void PaintVerifiedBodySlots(GameObject model,
            AarisVehicleRecord record)
        {
            Color paint = BodyPaintColours[StablePaintVariant(
                record.point.id, record.type.id)];
            // Only executed at spawn, never per frame. A per-renderer indexed
            // property block keeps the original shared URP PBR material/normal
            // maps intact and does not allocate unique material instances.
            // Paint-only per-car finish: never re-shade glass, wheel rubber,
            // light lenses, decals or the shared PBR texture atlas.
            float targetMetallic = .34f, targetSmoothness = .64f;
            float paintClearCoat = .31f;
            switch (record.type.id)
            {
                case "SUV":
                    targetMetallic = .25f; targetSmoothness = .55f;
                    paintClearCoat = .13f; break;
                case "Buggy":
                    targetMetallic = .23f; targetSmoothness = .47f;
                    paintClearCoat = .08f; break;
                case "Sports":
                    targetMetallic = .40f; targetSmoothness = .68f;
                    paintClearCoat = .36f; break;
                case "GT":
                    targetMetallic = .41f; targetSmoothness = .69f;
                    paintClearCoat = .41f; break;
                case "Hatchback": targetMetallic = .29f; targetSmoothness = .59f; break;
                case "Van": targetMetallic = .21f; targetSmoothness = .49f; break;
                case "Utility": targetMetallic = .21f; targetSmoothness = .50f; break;
            }
            var block = new MaterialPropertyBlock();
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    Material material = materials[slot];
                    if (material == null || !material.HasProperty("_BaseColor"))
                        continue;
                    string label = material.name;
                    bool separatePaint = label.IndexOf("AarisPaint",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    bool bodyAtlas = label.IndexOf("body_pbr",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    // The sedan has a separate, verified URP painted body
                    // rather than a body_pbr atlas. Keep its original PBR
                    // maps and let it take part in stable car paint variants.
                    bool civilianPaint = label.IndexOf("AarisCivilianSurface",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!separatePaint && !bodyAtlas && !civilianPaint) continue;
                    // Subtle for a multi-purpose body texture: paint variation
                    // must not wash out the authored windows/decals/headlights.
                    Color tint = SelectPaintTint(
                        material, paint, bodyAtlas, civilianPaint);
                    block.Clear();
                    renderer.GetPropertyBlock(block, slot);
                    block.SetColor("_BaseColor", tint);
                    // Only clearcoat separate body-paint panels: never tint
                    // glazing, lamps, wheel rubber or PBR body atlases.
                    if (separatePaint || civilianPaint)
                    {
                        if (material.HasProperty("_Smoothness"))
                            block.SetFloat("_Smoothness", Mathf.Clamp(
                                Mathf.Lerp(material.GetFloat("_Smoothness"),
                                    targetSmoothness, .76f), .38f, .78f));
                        if (material.HasProperty("_Metallic"))
                            block.SetFloat("_Metallic", Mathf.Clamp(
                                Mathf.Lerp(material.GetFloat("_Metallic"),
                                    targetMetallic, .76f), .16f, .50f));
                        if (material.HasProperty("_ClearCoatMask"))
                            block.SetFloat("_ClearCoatMask", paintClearCoat);
                    }
                    renderer.SetPropertyBlock(block, slot);
                }
            }
        }

        private void FallBackToVerifiedModel(int index, string reason)
        {
            if (index < 0 || safeBodyPrefabs == null ||
                safeWheelPrefabs == null ||
                safeBodyPrefabs[index] == null || safeWheelPrefabs[index] == null ||
                ReferenceEquals(prefabs[index], safeBodyPrefabs[index]))
                return;
            prefabs[index] = safeBodyPrefabs[index];
            wheelPrefabs[index] = safeWheelPrefabs[index];
            vehicleTypes[index].useEmbeddedAuthoredWheels = false;
            vehicleTypes[index].convertAuthoredBodyMaterials = false;
            vehicleTypes[index].preserveAuthoredUrpMaterials = false;
            Debug.LogWarning("Aaris vehicles: " + vehicleTypes[index].id +
                " authored model failed " + reason +
                "; using verified local car and wheels for future spawns.");
        }

        private bool HasReusableParkedVehicle(AarisVehicleType type,
            GameObject source, GameObject wheels, float now)
        {
            for (int i = parkedCache.Count - 1; i >= 0; i--)
            {
                ParkedVehicle parked = parkedCache[i];
                if (parked.motor != null && parked.expiresAt > now &&
                    ReferenceEquals(parked.type, type) &&
                    parked.body == source && parked.wheels == wheels)
                    return true;
            }
            return false;
        }

        private void EnsurePreparedBodyStagingRoot()
        {
            if (preparedBodyStagingRoot != null) return;
            preparedBodyStagingRoot = new GameObject("Aaris Async Vehicle Body Staging");
            preparedBodyStagingRoot.transform.SetParent(transform, false);
            // Parent inactivity prevents rendering, collisions, animation and
            // OnEnable while Unity prepares this exact authored body.
            preparedBodyStagingRoot.SetActive(false);
        }

        private void ApplyAsyncInstantiateIntegrationBudget()
        {
            if (asyncInstantiateBudgetOverridden) return;
            float current = AsyncInstantiateOperation.GetIntegrationTimeMS();
            if (!(current > AsyncVehicleIntegrationBudgetMs)) return;
            previousAsyncInstantiateIntegrationMs = current;
            AsyncInstantiateOperation.SetIntegrationTimeMS(
                AsyncVehicleIntegrationBudgetMs);
            asyncInstantiateBudgetOverridden = true;
        }

        private void RestoreAsyncInstantiateIntegrationBudget()
        {
            if (!asyncInstantiateBudgetOverridden) return;
            // Respect a different runtime controller if it changed the global
            // value after this request was admitted.
            float current = AsyncInstantiateOperation.GetIntegrationTimeMS();
            if (Mathf.Approximately(current, AsyncVehicleIntegrationBudgetMs))
                AsyncInstantiateOperation.SetIntegrationTimeMS(
                    previousAsyncInstantiateIntegrationMs);
            asyncInstantiateBudgetOverridden = false;
        }

        private bool StartPreparedBodyClone(int typeIndex, float now)
        {
            // Reuse the existing low-memory cooldown: after Android reports
            // pressure, do not recreate hidden speculative geometry for 60 s.
            // Visible/required vehicles still use the ordinary spawn path.
            if (now < parkedCacheAllowedAt ||
                preparedBodyOperation != null || preparedBodyClone != null ||
                typeIndex < 0 || typeIndex >= vehicleTypes.Length ||
                !authoredBodyResolved[typeIndex])
                return false;
#if UNITY_ANDROID && !UNITY_EDITOR
            // This hidden clone is always optional. Under severe thermal
            // pressure save CPU/native-memory headroom for the required visible
            // spawn path instead of doing speculative hierarchy preparation.
            if (AarisAndroidThermalGovernor.SuspendSpeculativePrefetch)
                return false;
#endif

            GameObject source = prefabs[typeIndex];
            GameObject wheels = wheelPrefabs[typeIndex];
            AarisVehicleType type = vehicleTypes[typeIndex];
            if (source == null || wheels == null || type == null ||
                HasReusableParkedVehicle(type, source, wheels, now))
                return false;

            // Snapshot creation is optional native work too. Use the existing
            // global admission slot so prewarm cannot create a new hitch.
            if (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                return false;

            EnsurePreparedBodyStagingRoot();
            var parameters = new InstantiateParameters
            {
                parent = preparedBodyStagingRoot.transform,
                worldSpace = false,
                // These Resources prefabs are immutable source assets here;
                // Unity 6 can therefore serialize the snapshot asynchronously.
                originalImmutable = true
            };
            try
            {
                ApplyAsyncInstantiateIntegrationBudget();
                preparedBodyOperation = UnityEngine.Object.InstantiateAsync(
                    source, parameters, CancellationToken.None);
                preparedBodyOperation.priority = -1;
                preparedBodyOperation.allowSceneActivation = false;
                preparedBodySource = source;
                preparedBodyExpiresAt = now + PreparedBodyLifetimeSeconds;
                AsyncPreparedBodyStarts++;
                return true;
            }
            catch (Exception e)
            {
                preparedBodyOperation = null;
                preparedBodySource = null;
                RestoreAsyncInstantiateIntegrationBudget();
                if (Debug.isDebugBuild)
                    Debug.LogWarning("Aaris vehicles: async body prepare skipped (" +
                        e.GetType().Name + ").");
                return false;
            }
        }

        private void PumpPreparedBodyClone(float now)
        {
            if (preparedBodyClone != null)
            {
                if (now >= preparedBodyExpiresAt)
                    ReleasePreparedBodyPreparation(true);
                return;
            }

            AsyncInstantiateOperation<GameObject> operation = preparedBodyOperation;
            if (operation == null) return;
            if (now >= preparedBodyExpiresAt)
            {
                ReleasePreparedBodyPreparation(true);
                return;
            }

            if (!operation.isDone)
            {
                if (operation.IsWaitingForSceneActivation() &&
                    AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                {
                    // Final object integration/Awake runs only after a later
                    // clean rendered-frame admission.
                    operation.allowSceneActivation = true;
                }
                return;
            }

            RestoreAsyncInstantiateIntegrationBudget();
            preparedBodyOperation = null;
            GameObject[] result = operation.Result;
            GameObject ready = result != null && result.Length > 0 ? result[0] : null;
            if (result != null)
            {
                for (int i = 1; i < result.Length; i++)
                    if (result[i] != null) Destroy(result[i]);
            }
            if (ready == null)
            {
                preparedBodySource = null;
                return;
            }
            preparedBodyClone = ready;
            preparedBodyExpiresAt = now + PreparedBodyLifetimeSeconds;
        }

        private void ReleasePreparedBodyPreparation(bool reserveTeardownFrame)
        {
            AsyncInstantiateOperation<GameObject> operation = preparedBodyOperation;
            preparedBodyOperation = null;
            if (operation != null)
            {
                if (!operation.isDone)
                    operation.Cancel();
                else
                {
                    GameObject[] result = operation.Result;
                    if (result != null)
                        for (int i = 0; i < result.Length; i++)
                            if (result[i] != null) Destroy(result[i]);
                }
            }
            if (preparedBodyClone != null)
                Destroy(preparedBodyClone);
            preparedBodyClone = null;
            preparedBodySource = null;
            preparedBodyExpiresAt = 0f;
            RestoreAsyncInstantiateIntegrationBudget();
            if (reserveTeardownFrame)
                AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();
        }

        private bool TryConsumePreparedBody(GameObject source, Transform parent,
            out GameObject clone)
        {
            clone = null;
            if (preparedBodyClone == null || source == null ||
                preparedBodySource != source)
                return false;
            clone = preparedBodyClone;
            preparedBodyClone = null;
            preparedBodySource = null;
            preparedBodyExpiresAt = 0f;
            // Reparent(false) keeps the same local transform contract as the
            // synchronous Instantiate(source, root.transform, false) fallback.
            clone.transform.SetParent(parent, false);
            AsyncPreparedBodyConsumes++;
            return true;
        }

        private int FindPreparedBodyType(Vector3 position,
            Vector3 predictedPosition, float now)
        {
            if (preparedBodyOperation != null || preparedBodyClone != null)
                return -1;

            int bestType = -1;
            float bestDistanceSq = AuthoredPrewarmRadius * AuthoredPrewarmRadius;
            for (int i = 0; i < plans.Count; i++)
            {
                AarisVehicleRecord record = plans[i];
                if (record.visible != null) continue;
                int typeIndex = TypeIndex(record.type);
                if (typeIndex < 0 || !authoredBodyResolved[typeIndex] ||
                    prefabs[typeIndex] == null || wheelPrefabs[typeIndex] == null)
                    continue;
                float distanceSq = (record.position - position).sqrMagnitude;
                if (distanceSq > bestDistanceSq ||
                    HasReusableParkedVehicle(record.type, prefabs[typeIndex],
                        wheelPrefabs[typeIndex], now))
                    continue;
                bestDistanceSq = distanceSq;
                bestType = typeIndex;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            // Prediction prepares only an invisible clone. Real position still
            // exclusively controls spawning/physics; thermal pressure disables
            // this speculative branch.
            if (bestType < 0 && predictedPosition != position &&
                !AarisAndroidThermalGovernor.SuspendSpeculativePrefetch)
            {
                float predictedBestSq =
                    AuthoredPrewarmRadius * AuthoredPrewarmRadius;
                for (int i = 0; i < plans.Count; i++)
                {
                    AarisVehicleRecord record = plans[i];
                    if (record.visible != null) continue;
                    int typeIndex = TypeIndex(record.type);
                    if (typeIndex < 0 || !authoredBodyResolved[typeIndex] ||
                        prefabs[typeIndex] == null || wheelPrefabs[typeIndex] == null)
                        continue;
                    float distanceSq =
                        (record.position - predictedPosition).sqrMagnitude;
                    if (distanceSq > predictedBestSq ||
                        HasReusableParkedVehicle(record.type, prefabs[typeIndex],
                            wheelPrefabs[typeIndex], now))
                        continue;
                    predictedBestSq = distanceSq;
                    bestType = typeIndex;
                }
            }
#endif
            return bestType;
        }

        private bool Build(AarisVehicleRecord record, Vector3 pos)
        {
            int typeIndex = TypeIndex(record.type);
            if (typeIndex < 0 || !authoredBodyResolved[typeIndex]) return false;
            GameObject source = prefabs[typeIndex];
            GameObject wheelSource = wheelPrefabs[typeIndex];
            if (source == null || wheelSource == null) return false;
            if (TryReuseParkedVehicle(record, pos, source, wheelSource)) return true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var profilerScope = k_VehicleBuild.Auto();
#endif
            var root = new GameObject("Aaris Vehicle " + record.type.id + " " + record.point.id);
            root.transform.SetParent(transform, false);
            root.transform.SetPositionAndRotation(pos, record.rotation);
            GameObject mesh;
            if (!TryConsumePreparedBody(source, root.transform, out mesh))
                mesh = Instantiate(source, root.transform, false);
            mesh.name = record.type.preserveAuthoredUrpMaterials ||
                record.type.useEmbeddedAuthoredWheels ||
                record.type.convertAuthoredBodyMaterials
                ? "Authored Vehicle Body" : "Local CC0 Car Model";
            mesh.GetComponentsInChildren<Collider>(true, buildColliders);
            for (int i = 0; i < buildColliders.Count; i++)
                if (buildColliders[i] != null) buildColliders[i].enabled = false;
            mesh.GetComponentsInChildren<Renderer>(true, buildRenderers);
            var renderers = buildRenderers;
            if (!HasRenderableVehicleBody(renderers))
            {
                buildColliders.Clear();
                buildRenderers.Clear();
                FallBackToVerifiedModel(typeIndex, "renderer check");
                Destroy(root);
                return false;
            }
            // Kenney Car Kit uses one UV palette atlas. Share ONE URP Lit
            // material across streamed instances; never instantiate new
            // materials per car/per frame.
            if (record.type.useEmbeddedAuthoredWheels ||
                record.type.convertAuthoredBodyMaterials ||
                record.type.preserveAuthoredUrpMaterials)
            {
                ApplyAuthoredMaterials(renderers);
            }
            else if (!record.type.preserveAuthoredUrpMaterials)
            {
                foreach (var renderer in renderers)
                {
                    int submeshes = VisualSubmeshCount(renderer);
                    if (!paletteSlotLayouts.TryGetValue(submeshes, out Material[] materials))
                    {
                        materials = new Material[submeshes];
                        for (int i = 0; i < submeshes; i++) materials[i] = paletteMaterial;
                        paletteSlotLayouts.Add(submeshes, materials);
                    }
                    renderer.sharedMaterials = materials;
                }
            }
            // Paint re-queries visuals, so no cached reference keeps a retired car alive.
            buildColliders.Clear();
            buildRenderers.Clear();
            PaintVerifiedBodySlots(mesh, record);
            // Inspect the model in chassis space; world-space AABBs change
            // when the authored road marker is rotated 45/90/135 degrees.
            if (!TryGetVisualBoundsLocal(root.transform, out Bounds bounds))
            {
                FallBackToVerifiedModel(typeIndex, "visual bounds");
                Destroy(root);
                return false;
            }
            // Source evidence: five CC0 FBX shells store vehicle length on
            // their Y axis and height on Z. Some Unity FBX importers already
            // convert those axes to Y-up; others retain the raw geometry.
            // Decide from the actual VISIBLE mesh bounds in chassis space,
            // never blindly rotate the sedan, GT or a corrected FBX twice.
            if (bounds.size.y > 2.75f &&
                bounds.size.y > Mathf.Max(bounds.size.x, bounds.size.z) * 1.30f &&
                bounds.size.x > .8f && bounds.size.z > .7f)
            {
                mesh.transform.localRotation =
                    Quaternion.Euler(-90f, 0f, 0f) * mesh.transform.localRotation;
                if (!TryGetVisualBoundsLocal(root.transform, out bounds))
                {
                    FallBackToVerifiedModel(typeIndex, "source axis correction");
                    Destroy(root);
                    return false;
                }
            }
            if (bounds.size.x > bounds.size.z * 1.1f)
                mesh.transform.localRotation =
                    Quaternion.Euler(0f, 90f, 0f) * mesh.transform.localRotation;
            if (record.type.useEmbeddedAuthoredWheels)
                FaceEmbeddedVehicleForward(root.transform, mesh.transform);
            if (!TryGetVisualBoundsLocal(root.transform, out bounds) ||
                bounds.size.z < .1f || bounds.size.x < .1f)
            {
                FallBackToVerifiedModel(typeIndex, "model orientation");
                Destroy(root);
                return false;
            }
            // A hatchback, GT and tall SUV must not all be treated as the
            // same 1.88 x 4.28 m sedan. Fit each authored silhouette uniformly
            // inside a reviewed real-metre envelope; never stretch axles,
            // wheels, doors or UVs separately. Terrain corner probes and
            // occupied road clearance use these SAME class-size metres.
            float widthMetres = record.type.referenceWidthMetres > 0f
                ? record.type.referenceWidthMetres : 1.88f;
            float lengthMetres = record.type.referenceLengthMetres > 0f
                ? record.type.referenceLengthMetres : 4.28f;
            widthMetres = Mathf.Clamp(widthMetres, 1.65f, 2.15f);
            lengthMetres = Mathf.Clamp(lengthMetres, 3.35f, 5.50f);
            float scale = Mathf.Min(widthMetres / bounds.size.x,
                lengthMetres / bounds.size.z);
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
            {
                FallBackToVerifiedModel(typeIndex, "invalid model scale");
                Destroy(root);
                return false;
            }
            // The CC0 Premium GT has a wider shoulder/mirror envelope
            // than its 4.63 m closed-coupe length. Uniform scaling to the
            // width cap would compress it into a short, toy-like 3.87 m
            // car with a sub-metre roof. Correct lateral width separately
            // ONLY for this verified embedded-wheel model. Crucially,
            // Y and Z use the same ratio: circular rolling tyres remain
            // circular and WheelCollider radius follows the real mesh.
            if (record.type.id == "GT" &&
                record.type.useEmbeddedAuthoredWheels)
            {
                float lateral = Mathf.Clamp(widthMetres / bounds.size.x,
                    .01f, 50f);
                float rolling = Mathf.Clamp(lengthMetres / bounds.size.z,
                    .01f, 50f);
                mesh.transform.localScale = Vector3.Scale(
                    mesh.transform.localScale, new Vector3(lateral, rolling, rolling));
            }
            else
            {
                // All other cars preserve original uniform isotropic fit.
                mesh.transform.localScale *= Mathf.Clamp(scale, .01f, 50f);
            }
            if (!TryGetVisualBoundsLocal(root.transform, out bounds))
            {
                FallBackToVerifiedModel(typeIndex, "model scale");
                Destroy(root);
                return false;
            }
            // Keep the body centered in chassis X/Z without dropping the
            // tyres to an arbitrary pivot height. Actual authored wheel
            // bottoms subsequently anchor the entire body to the road plane.
            mesh.transform.localPosition += new Vector3(0f, .48f, 0f) - bounds.center;
            // A misimported or tilted axle set must NOT silently continue.
            // Without this guard an unsuccessful tyre-ground fit leaves a
            // vehicle hovering or clipping the road while still marked valid.
            // Degrade the art to the existing verified local prefab on the
            // next spawn rather than destroying any saved vehicle state.
            if (record.type.useEmbeddedAuthoredWheels &&
                !AlignEmbeddedTyresToRoad(root.transform, mesh.transform))
            {
                FallBackToVerifiedModel(typeIndex, "tyre ground alignment");
                Destroy(root);
                return false;
            }
            if (!TryGetVisualBoundsLocal(root.transform, out bounds))
            {
                FallBackToVerifiedModel(typeIndex, "aligned model bounds");
                Destroy(root);
                return false;
            }
            // A non-empty renderer is not enough: a broken importer can
            // silently create a 30-cm-tall car or an oversized bus while all
            // earlier wheel material/bounds checks return success. Use the
            // exact final chassis-space mesh dimensions, AFTER source-axis,
            // body scaling and tyre-ground alignment, before creating physics.
            // The original verified fallback kit keeps its legacy footprint.
            bool importedCar = record.type.useEmbeddedAuthoredWheels ||
                record.type.preserveAuthoredUrpMaterials ||
                record.type.convertAuthoredBodyMaterials;
            if (importedCar && !HasPlausibleAuthoredSilhouette(
                    bounds.size, widthMetres, lengthMetres))
            {
                FallBackToVerifiedModel(typeIndex, "unrealistic car silhouette");
                Destroy(root);
                return false;
            }
            var motor = root.AddComponent<AarisVehicleMotor>();
            motor.Configure(record.type.id, record.type.maxSpeedMetres, record.type.massKg);
            motor.ConfigureHandling(record.type.drivetrain,
                record.type.tyreGripScale, record.type.suspensionScale,
                record.type.steeringScale, record.type.suspensionTravelScale);
            // After the mesh is recentered to local Y=.48, its size remains
            // valid in chassis space at every road yaw. Align physical shell
            // and wheels to the visible type instead of a generic sedan.
            motor.FitShellAndWheelbase(bounds);
            bool wheelsAttached = record.type.useEmbeddedAuthoredWheels
                ? motor.AttachEmbeddedWheelModels(mesh.transform)
                : motor.AttachWheelModels(wheelSource, wheelPaletteMaterial,
                    record.type.preserveAuthoredUrpMaterials);
            if (!wheelsAttached)
            {
                FallBackToVerifiedModel(typeIndex, "wheel attachment");
                Destroy(root);
                return false;
            }
            // Only the genuine FBX chassis banks/squats visually; all four
            // real wheel renderers have already been detached to Rigidbody
            // space by AttachEmbeddedWheelModels. This keeps tire contact and
            // physics fixed while headlights/exhaust follow the body itself.
            motor.ConfigureBodyDynamics(mesh.transform);
            // Do this only after the body was reoriented, scaled, and the
            // authored wheel set passed inspection. Lamp placement is in the
            // same fitted local chassis space as the physical wheelbase.
            Bounds lampBounds = bounds;
            if (record.type.useEmbeddedAuthoredWheels &&
                TryGetAuthoredLampBodyBounds(root.transform, mesh.transform,
                    lampBounds, out Bounds bodyOnly))
                lampBounds = bodyOnly;
            motor.ConfigureExteriorLights(lampBounds,
                headlampGlassMaterial, headlampOnMaterial,
                taillampGlassMaterial, brakeLampMaterial,
                reverseLampMaterial, sharedLampLensMesh, lampBezelMaterial,
                player);
            motor.ConfigureRearTyreTraces(skidMarkMaterial);
            motor.ConfigureExhaust(exhaustMaterial, bounds);
            motor.RestoreStreamedDamage(record.damage01);
            motor.ConfigureBodyWear(mesh.transform, BodyPaintColours[
                StablePaintVariant(record.point.id, record.type.id)]);
            FreshVehicleBuilds++;
            BindVehicle(record, motor, source, wheelSource, bounds);
            return true;
        }

        private bool TryReuseParkedVehicle(AarisVehicleRecord record, Vector3 pos,
            GameObject source, GameObject wheelSource)
        {
            float now = Time.unscaledTime;
            if (now < parkedCacheAllowedAt) return false;
            for (int i = parkedCache.Count - 1; i >= 0; i--)
            {
                ParkedVehicle parked = parkedCache[i];
                // Always match the validated body AND wheel assets. A later
                // fallback must not resurrect a rejected authored import.
                if (parked.motor == null || parked.expiresAt <= now ||
                    !ReferenceEquals(parked.type, record.type) ||
                    parked.body != source || parked.wheels != wheelSource)
                    continue;
                parkedCache.RemoveAt(i);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                using var profilerScope = k_VehicleReuse.Auto();
#endif
                parked.motor.gameObject.name =
                    "Aaris Vehicle " + record.type.id + " " + record.point.id;
                parked.motor.ReactivateParkedVehicle(pos, record.rotation,
                    record.damage01, BodyPaintColours[
                        StablePaintVariant(record.point.id, record.type.id)]);
                ReusedVehicleBuilds++;
                BindVehicle(record, parked.motor, source, wheelSource, parked.bounds);
                return true;
            }
            return false;
        }

        private static void BindVehicle(AarisVehicleRecord record, AarisVehicleMotor motor,
            GameObject source, GameObject wheelSource, Bounds bounds)
        {
            record.visible = motor;
            record.builtBody = source;
            record.builtWheels = wheelSource;
            record.builtBounds = bounds;
            if (Debug.isDebugBuild)
            {
                Debug.Log("AARIS_CAR_PLACED " + record.point.id + " " +
                    record.type.id + " " + motor.transform.position.ToString("F2"));
                // One spawn-time source of truth for native Unity QA:
                // compare these actual rendered metres against catalog cars.
                // Debug-only, never a 30/60 FPS render or driving loop cost.
                Debug.Log("AARIS_CAR_RENDER_METRES id=" + record.type.id +
                    " width=" + bounds.size.x.ToString("F2") +
                    " height=" + bounds.size.y.ToString("F2") +
                    " length=" + bounds.size.z.ToString("F2"));
            }
        }

        private void RetireVehicle(AarisVehicleRecord record, float now)
        {
            AarisVehicleMotor motor = record.visible;
            record.visible = null;
            GameObject body = record.builtBody, wheels = record.builtWheels;
            record.builtBody = record.builtWheels = null;
            if (motor == null) return;
            // Colliders and renderers leave the world immediately, even if
            // Unity has to finish a native destruction later in this frame.
            motor.gameObject.SetActive(false);
            if (now < parkedCacheAllowedAt || body == null || wheels == null)
            {
                Destroy(motor.gameObject);
                AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();
                return;
            }
            if (parkedCache.Count >= parkedCacheLimit)
                DestroyOldestParkedVehicle();
            parkedCache.Add(new ParkedVehicle
            {
                motor = motor, type = record.type, body = body,
                wheels = wheels, bounds = record.builtBounds,
                expiresAt = now + ParkedCacheLifetime
            });
        }

        private void DestroyOldestParkedVehicle()
        {
            AarisVehicleMotor oldest = parkedCache[0].motor;
            parkedCache.RemoveAt(0);
            if (oldest == null) return;
            Destroy(oldest.gameObject);
            AarisMobileStreamingFrameGate.ReserveNativeTeardownFrame();
        }

        private void Update()
        {
            // The mobile prewarmer can span multiple rendered frames. Never
            // poll empty plan/type arrays until Setup validates the catalog.
            if (player == null || vehicleTypes == null || prefabs == null) return;
            float now = Time.unscaledTime;
            if (lowMemoryPending)
            {
                parkedCacheAllowedAt = now + 60f;
                lowMemoryPending = false;
                // Critical memory beats speculative smoothness. Remove/cancel
                // only the one hidden prepared body; visible cars and original
                // source assets are untouched.
                ReleasePreparedBodyPreparation(true);
                return;
            }
            // Constant-time operation-state check: final clone integration can
            // use the first clean rendered frame instead of waiting 300 ms.
            PumpPreparedBodyClone(now);
            if (parkedCache.Count > 0 && (now < parkedCacheAllowedAt ||
                parkedCache[0].expiresAt <= now))
            {
                // Bound native destruction to one expired/cached car per
                // rendered frame. Active cars and their saved state stay live.
                DestroyOldestParkedVehicle();
                return;
            }
            if (now < nextPoll) return;
            nextPoll = now + .3f;
            // One native Transform read per poll, not one per vehicle record.
            Vector3 playerPosition = player.position;
#if UNITY_ANDROID && !UNITY_EDITOR
            // Sample once per existing 300 ms poll; never add Update-time
            // physics, visibility queries, allocations or predictive spawns.
            Vector3 predictedPrewarmPosition = playerPosition;
            if (hasPrewarmSample)
            {
                float elapsed = now - previousPrewarmTime;
                if (elapsed >= 0.15f && elapsed <= 1.5f)
                {
                    Vector3 movement = playerPosition - previousPrewarmPosition;
                    float teleportLimit =
                        VehiclePrewarmTeleportSpeedMetresPerSecond * elapsed;
                    // Treat teleports and resuming a suspended app as resets,
                    // not as demand to import distant, invisible cars.
                    if (movement.sqrMagnitude <= teleportLimit * teleportLimit)
                    {
                        Vector3 velocity = movement / elapsed;
                        if (velocity.sqrMagnitude > 1f)
                            predictedPrewarmPosition += Vector3.ClampMagnitude(
                                velocity, VehiclePrewarmMaxSpeedMetresPerSecond) *
                                VehiclePrewarmLeadSeconds;
                    }
                }
            }
            previousPrewarmPosition = playerPosition;
            previousPrewarmTime = now;
            hasPrewarmSample = true;
#endif
            int active = 0, built = 0, unloaded = 0;
            int spawnProbes = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
            // Compare pacing to the actual display/target combination, not
            // a fixed 45 FPS threshold. On 60 Hz at target 30 FPS, a healthy
            // 33 ms frame must not be mistaken for an overloaded frame; on
            // 90 Hz at requested 60 FPS the actual pacing can be 45 FPS.
            // The small jitter allowance avoids flipping admission at equality.
            // No visual-range, model-count, resolution or allocation changes.
            bool constrainedFrame = Time.smoothDeltaTime >
                AarisMobileStreamingFrameGate.CurrentPacedFrameBudgetSeconds() * 1.08f;
#else
            const bool constrainedFrame = false;
#endif
            foreach (var record in plans)
            {
                if (record.visible == null) continue;
                float dist2 = (record.visible.transform.position - playerPosition).sqrMagnitude;
                // Never despawn a driven, rolling or collided vehicle.
                if (unloaded < unloadBudgetPerPoll &&
                    dist2 > UnloadRadius * UnloadRadius && record.visible.CanPark)
                {
                    record.position = record.visible.transform.position;
                    record.rotation = record.visible.transform.rotation;
                    record.damage01 = record.visible.Damage01;
                    RetireVehicle(record, now);
                    unloaded++;
                }
                else active++;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            // Unregistering four WheelColliders still touches PhysX even if
            // the car enters the cache. Keep this poll off the native load slot.
            if (unloaded > 0 && AarisMobileStreamingFrameGate.IsConstrainedDevice())
            {
                AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();
                return;
            }
#endif
            PumpAuthoredBodyLoads();
            // Compete for a single new preload slot per poll by actual
            // distance, not the randomized order of the match's garage plans.
            int closestType = -1;
            float closestDistanceSq = AuthoredPrewarmRadius * AuthoredPrewarmRadius;
            foreach (var record in plans)
            {
                float distanceSq = (record.position - playerPosition).sqrMagnitude;
                if (distanceSq > closestDistanceSq ||
                    (distanceSq == closestDistanceSq && closestType >= 0)) continue;
                int typeIndex = TypeIndex(record.type);
                if (typeIndex < 0 || authoredBodyResolved[typeIndex] ||
                    authoredBodyRequests[typeIndex] != null) continue;
                closestType = typeIndex;
                closestDistanceSq = distanceSq;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            // Prioritize every currently nearby authored car first. Only when
            // none needs a model may one approaching car borrow the SAME async
            // loading slot. Real player position still exclusively controls
            // physics, placement, visibility, load and unload distances.
            if (closestType < 0 && predictedPrewarmPosition != playerPosition)
            {
                // Only the *predicted-only* request is suppressed under
                // severe OS thermal pressure. The actual-position 150 m
                // model prewarm above remains unchanged.
                if (!AarisAndroidThermalGovernor.SuspendSpeculativePrefetch)
                {
                    float predictedNearestSq =
                    AuthoredPrewarmRadius * AuthoredPrewarmRadius;
                foreach (var record in plans)
                {
                    float distanceSq =
                        (record.position - predictedPrewarmPosition).sqrMagnitude;
                    if (distanceSq > predictedNearestSq) continue;
                    int typeIndex = TypeIndex(record.type);
                    if (typeIndex < 0 || authoredBodyResolved[typeIndex] ||
                        authoredBodyRequests[typeIndex] != null) continue;
                    closestType = typeIndex;
                    predictedNearestSq = distanceSq;
                }
                }
            }
#endif
            if (closestType >= 0) StartAuthoredBodyPrewarm(closestType);
#if UNITY_ANDROID && !UNITY_EDITOR
            if (AarisMobileStreamingFrameGate.IsConstrainedDevice())
            {
                int preparedType = FindPreparedBodyType(
                    playerPosition, predictedPrewarmPosition, now);
                if (preparedType >= 0)
                    StartPreparedBodyClone(preparedType, now);
            }
#endif

            // The catalog is in match plan order, NOT player distance order.
            // On a low-end phone with a two-build budget, always give the
            // closest ready car the first slot, even in a crowded sector.
            // Scan at most 44 immutable plans per attempt; no sorting or GC.
#if UNITY_ANDROID && !UNITY_EDITOR
            // A 300ms vehicle poll can otherwise stack multiple expensive
            // ground-ray and overlap probes on the same low-end CPU frame.
            double spawnProbeSliceStart = Time.realtimeSinceStartupAsDouble;
#endif
            while (built < spawnBudgetPerPoll &&
                   spawnProbes < spawnProbeBudgetPerPoll &&
                   (!constrainedFrame || (built == 0 && spawnProbes < 3)))
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // Never defer the first attempt. Remaining candidates retain
                // their normal retry state for the next 300ms poll; no car,
                // authored detail, visibility radius or collider is removed.
                if (spawnProbes > 0 &&
                    AarisMobileStreamingFrameGate.IsConstrainedDevice() &&
                    Time.realtimeSinceStartupAsDouble - spawnProbeSliceStart >=
                    AndroidSpawnProbeSliceSeconds)
                    break;
#endif
                AarisVehicleRecord record = null;
                float nearestDistanceSq = LoadRadius * LoadRadius;
                foreach (var pending in plans)
                {
                    if (pending.visible != null || now < pending.nextProbe) continue;
                    float distanceSq = (pending.position - playerPosition).sqrMagnitude;
                    if (distanceSq > nearestDistanceSq ||
                        (distanceSq == nearestDistanceSq && record != null)) continue;
                    int pendingType = TypeIndex(pending.type);
                    if (pendingType >= 0 && !authoredBodyResolved[pendingType]) continue;
                    nearestDistanceSq = distanceSq;
                    record = pending;
                }
                if (record == null) break;

                AarisVehicleRecord parkedVictim = null;
                if (active >= MaximumNearbyVehicles)
                {
                    if (unloaded >= unloadBudgetPerPoll) break;
                    parkedVictim = FarthestReplaceableParkedCar(
                        playerPosition, nearestDistanceSq);
                    // If the closest candidate cannot win a slot by 20m,
                    // no farther candidate can win one in this poll either.
                    if (parkedVictim == null) break;
                }

                spawnProbes++;
                if (!TryResolvePlanned(record, out Vector3 pos))
                {
                    record.nextProbe = now + 3.0f;
                    continue;
                }
                // A physics wake or another collision may make the selected
                // victim non-parkable while we probe the replacement spot.
                // Refuse to remove a rolling/occupied vehicle.
                if (parkedVictim != null &&
                    (parkedVictim.visible == null || !parkedVictim.visible.CanPark))
                {
                    record.nextProbe = now + .3f;
                    continue;
                }

                // Build the replacement FIRST. A broken authored FBX,
                // invisible renderer or missing wheel geometry can still
                // fail here, even after five-ray placement succeeded.
                // Evicting the old car first would leave the player with
                // zero visible vehicles in a formerly full 10-car stream.
#if UNITY_ANDROID && !UNITY_EDITOR
                // Avoid overlapping heavy builds across independent streamers.
                if (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                {
                    record.nextProbe = now + .15f;
                    break;
                }
#endif
                if (!Build(record, pos))
                {
                    record.nextProbe = now + 3.0f;
                    continue;
                }

                if (parkedVictim != null)
                {
                    AarisVehicleMotor retired = parkedVictim.visible;
                    if (retired == null || !retired.CanPark)
                    {
                        // Defensive rollback if physics ownership changed
                        // during Build. Do not destroy/replace an occupied or
                        // moving car. Build assigned this candidate's visible
                        // instance only after the wheel/model checks passed.
                        RetireVehicle(record, now);
                        record.nextProbe = now + .3f;
                        continue;
                    }
                    parkedVictim.position = retired.transform.position;
                    parkedVictim.rotation = retired.transform.rotation;
                    parkedVictim.damage01 = retired.Damage01;
                    parkedVictim.nextProbe = now + 3.0f;
                    RetireVehicle(parkedVictim, now);
                    unloaded++;
                    active--;
                }
                active++;
                built++;
                record.position = pos;
                record.previouslySpawned = true;
            }
        }

        // Only an unoccupied, stationary car beyond a distance hysteresis
        // can yield a slot. This is a bounded scan of the 44 match plans and
        // never takes away a driver's car or a nearby visible parked model.
        private AarisVehicleRecord FarthestReplaceableParkedCar(
            Vector3 playerPosition, float candidateDistanceSq)
        {
            const float PriorityMarginMetres = 20f;
            float minimumVictimDistance =
                Mathf.Sqrt(candidateDistanceSq) + PriorityMarginMetres;
            float farthestDistanceSq = minimumVictimDistance * minimumVictimDistance;
            AarisVehicleRecord farthest = null;
            foreach (var record in plans)
            {
                AarisVehicleMotor car = record.visible;
                if (car == null || !car.CanPark) continue;
                float distanceSq = (car.transform.position - playerPosition).sqrMagnitude;
                if (distanceSq <= farthestDistanceSq) continue;
                farthest = record;
                farthestDistanceSq = distanceSq;
            }
            return farthest;
        }

        private void OnDestroy()
        {
            ReleasePreparedBodyPreparation(false);
            if (paletteMaterial != null) Destroy(paletteMaterial);
            if (wheelPaletteMaterial != null) Destroy(wheelPaletteMaterial);
            if (headlampGlassMaterial != null) Destroy(headlampGlassMaterial);
            if (headlampOnMaterial != null) Destroy(headlampOnMaterial);
            if (taillampGlassMaterial != null) Destroy(taillampGlassMaterial);
            if (brakeLampMaterial != null) Destroy(brakeLampMaterial);
            if (reverseLampMaterial != null) Destroy(reverseLampMaterial);
            if (lampBezelMaterial != null) Destroy(lampBezelMaterial);
            if (sharedLampLensMesh != null) Destroy(sharedLampLensMesh);
            if (skidMarkMaterial != null) Destroy(skidMarkMaterial);
            if (exhaustMaterial != null) Destroy(exhaustMaterial);
            // These runtime URP conversions are shared among streamed car
            // instances, not imported asset materials. Release exactly the
            // converted copies on scene teardown to avoid native GPU leaks.
            foreach (Material converted in authoredMaterialCache.Values)
                if (converted != null) Destroy(converted);
            authoredMaterialCache.Clear();
        }

        public AarisVehicleMotor NearestAvailable(Vector3 origin, float maxDistance)
        {
            AarisVehicleMotor nearest = null;
            float best = maxDistance * maxDistance;
            foreach (var record in plans)
            {
                // A disabled Motor can retain a valid parked wheel pose but
                // cannot accept a live driver. Skip it before distance/ground
                // queries and continue seeking other usable cars.
                if (record.visible == null || !record.visible.isActiveAndEnabled ||
                    record.visible.IsOccupied) continue;
                float d = (record.visible.transform.position - origin).sqrMagnitude;
                // An unusable closer car must not hide a safe DRIVE target.
                // Check distance first to avoid wheel-ground probes for far cars.
                if (d >= best || !record.visible.IsSafeToExit) continue;
                best = d;
                nearest = record.visible;
            }
            return nearest;
        }
    }
}
