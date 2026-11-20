using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview
{
    /// <summary>
    /// A bounded stream of PARKED motorcycle scenery, not rideable bikes.
    /// Two-wheel driving must never be faked with the four-wheel car motor.
    /// </summary>
    [DefaultExecutionOrder(-60), DisallowMultipleComponent]
    public sealed class AarisRoadsideBikeScenery : MonoBehaviour
    {
        private const string ModelResource = "AarisVehicles/Authored/AarisRoadBike";
        private const int MaximumVisibleBikes = 3;
        private const float LoadRadius = 45f;
        // Start the same authored model import before it becomes visible.
        // This moves the expensive Resources load out of the scene boot frame.
        private const float PrewarmRadius = 110f;
        private const float UnloadRadius = 66f;
        private readonly List<BikePlan> plans = new List<BikePlan>(18);
        private readonly RaycastHit[] supportHits = new RaycastHit[20];
        private readonly RaycastHit[] denseSupportHits = new RaycastHit[96];
        private readonly Collider[] blockingHits = new Collider[24];
        private readonly Collider[] denseBlockingHits = new Collider[96];
        private readonly Material[] surfaces = new Material[8];
        private readonly Material[] paintVariants = new Material[4];
        // Pool at most three original motorcycle rigs instead of destroying
        // and re-importing their meshes every time the player changes roads.
        private readonly Stack<GameObject> parkedBikePool =
            new Stack<GameObject>(MaximumVisibleBikes);
        private readonly Dictionary<GameObject, BikeVisualCache> bikeVisuals =
            new Dictionary<GameObject, BikeVisualCache>(MaximumVisibleBikes);
        private Transform player;
        private AarisNearbyColliders nearbyColliders;
        private GameObject bikeSource;
        private ResourceRequest bikePrewarm;
        // A material-integration slice may span several frames on budget GPUs.
        // Never render a bike with a partially initialized material palette.
        private bool bikeMaterialsReady;
        // A GameObject deactivate stops the staged coroutine, but keeps the
        // imported motorcycle source. Track that job to allow a safe restart.
        private Coroutine bikeMaterialsBuild;
        private float nextPoll;
        private bool lowMemoryPending;
        private float poolAllowedAt;

        private sealed class BikePlan
        {
            internal AarisVehiclePoint point;
            internal GameObject visible;
            internal float nextProbe;
        }

        private sealed class BikeVisualCache
        {
            internal Renderer[] renderers;
            // Cached original surface slots for all four authored paint finishes.
            // Repainting a recycled bike never instantiates a new Material.
            internal Material[][][] paintLayouts;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            AarisThirdPersonPlayer motor = FindFirstObjectByType<AarisThirdPersonPlayer>();
            if (motor == null || FindFirstObjectByType<AarisRoadsideBikeScenery>() != null)
                return;
            var root = new GameObject("Aaris Roadside Motorcycles (streamed scenery)");
            root.AddComponent<AarisRoadsideBikeScenery>().player = motor.transform;
        }

        private void Start()
        {
            nearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();
#if UNITY_ANDROID && !UNITY_EDITOR
            // Cars use the exact same authored candidate JSON. Join their
            // shared async request instead of reading it synchronously on
            // the opening map frame. No new bikes or 3D models are created.
            StartCoroutine(LoadRoadPlanAfterAndroidImport());
#else
            LoadRoadPlans(Resources.Load<TextAsset>("AarisVehicles/vehicle_candidates"));
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private System.Collections.IEnumerator LoadRoadPlanAfterAndroidImport()
        {
            yield return null; // render aircraft/terrain before parsing road plans
            yield return AarisVehiclePointManifest.LoadAndroidAsync();
            if (!isActiveAndEnabled) yield break;
            yield return null; // native request completion and JSON parse are separate frames
            LoadRoadPlans(AarisVehiclePointManifest.AndroidCached);
        }
#endif

        private void LoadRoadPlans(TextAsset data)
        {
            if (data == null)
            {
                Debug.LogWarning("Aaris bikes: road nodes missing.");
                enabled = false;
                return;
            }
            AarisVehiclePointsFile file;
            try { file = JsonUtility.FromJson<AarisVehiclePointsFile>(data.text); }
            catch (Exception e)
            {
                Debug.LogWarning("Aaris bikes: invalid road nodes (" + e.GetType().Name + ").");
                enabled = false;
                return;
            }
            if (file == null || file.schema != 1 || file.sizeMetres != 2000 ||
                file.candidates == null) { enabled = false; return; }
            int index = 0;
            foreach (var point in file.candidates)
            {
                if (point == null || point.kind != "roadside") continue;
                // More authored roads, same maximum of three visible models.
                if (index++ % 2 == 0) plans.Add(new BikePlan { point = point });
            }
        }

        // Desktop and non-budget devices keep the original immediate setup.
        // The iterator creates the exact same assets, but only yields for a
        // budget Android GPU when it has a fresh shared admission slot.
        private void SetupMaterials()
        {
            System.Collections.IEnumerator job = BuildMaterialsAcrossFrames(false);
            while (job.MoveNext()) { }
        }

        private System.Collections.IEnumerator BuildMaterialsAcrossFrames(bool stagger)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) { enabled = false; yield break; }
            Color[] tones = {
                new Color(.08f,.23f,.40f), new Color(.034f,.039f,.048f),
                new Color(.57f,.61f,.67f), new Color(.15f,.17f,.20f),
                new Color(.055f,.062f,.075f), new Color(.94f,.91f,.72f),
                new Color(.78f,.038f,.02f), new Color(.98f,.43f,.035f)
            };
            for (int i = 0; i < surfaces.Length; i++)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // Two existing surface materials per render frame. A yield
                // ensures the next claim cannot spend this frame twice.
                if (stagger && i > 0 && (i & 1) == 0)
                {
                    yield return null;
                    while (isActiveAndEnabled &&
                           !AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                        yield return null;
                    if (!isActiveAndEnabled) yield break;
                }
#endif
                surfaces[i] = new Material(shader)
                {
                    name = "Aaris Road Bike " + i,
                    enableInstancing = true
                };
                surfaces[i].SetColor("_BaseColor", tones[i]);
                // Separate rubber, satin bodywork, exposed alloy, leather
                // and glass; eight shared URP Lit materials for all bikes.
                surfaces[i].SetFloat("_Metallic",
                    i == 0 ? .48f : i == 2 ? .82f :
                    i == 3 ? .43f : .03f);
                surfaces[i].SetFloat("_Smoothness",
                    i == 0 ? .65f : i == 1 ? .14f :
                    i == 2 ? .72f : i == 3 ? .37f :
                    i == 4 ? .16f : .67f);
                // Parked, switched-off motorcycles are NOT floodlights.
                // Only the lens emits a faint authored colour without
                // extra shadowing or per-frame light GameObjects.
                if (i == 5 || i == 6)
                {
                    surfaces[i].SetColor("_EmissionColor", tones[i] * .085f);
                    surfaces[i].EnableKeyword("_EMISSION");
                }
            }
            // Paint-only clear coat gives fairing curvature a restrained
            // automotive specular lobe. Do not coat tyres, alloy, seat or lamps;
            // the four bike colours all reuse this one lit shader variant.
            if (surfaces[0].HasProperty("_ClearCoatMask"))
            {
                surfaces[0].SetFloat("_ClearCoatMask", .27f);
                if (surfaces[0].HasProperty("_ClearCoatSmoothness"))
                    surfaces[0].SetFloat("_ClearCoatSmoothness", .75f);
                surfaces[0].EnableKeyword("_CLEARCOAT");
            }
            // Three extra shared paint finishes; never create a material per bike.
#if UNITY_ANDROID && !UNITY_EDITOR
            // Clear-coat clones do not share the surface-import frame on
            // Snapdragon 680. No texture, paint or shader variant is changed.
            if (stagger)
            {
                yield return null;
                while (isActiveAndEnabled &&
                       !AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                    yield return null;
                if (!isActiveAndEnabled) yield break;
            }
#endif
            paintVariants[0] = surfaces[0];
            Color[] paints = {
                new Color(.08f,.23f,.40f),
                new Color(.36f,.054f,.062f),
                new Color(.095f,.11f,.13f),
                new Color(.05f,.30f,.26f)
            };
            for (int i = 1; i < paintVariants.Length; i++)
            {
                paintVariants[i] = new Material(surfaces[0])
                {
                    name = "Aaris Road Bike Paint " + i,
                    enableInstancing = true
                };
                paintVariants[i].SetColor("_BaseColor", paints[i]);
            }
            bikeMaterialsReady = true;
        }

        private void OnEnable() { Application.lowMemory += OnLowMemory; }

        private void OnDisable()
        {
            Application.lowMemory -= OnLowMemory;
            if (bikeMaterialsBuild != null)
            {
                StopCoroutine(bikeMaterialsBuild);
                bikeMaterialsBuild = null;
            }
            if (bikeMaterialsReady) return;
            // GameObject deactivation interrupts the budgeted coroutine.
            // Discard only its incomplete palette; otherwise its first
            // populated surface blocks a correct rebuild after reactivation.
            // The original vehicle resource and parking plans stay cached.
            for (int i = 1; i < paintVariants.Length; i++)
            {
                if (paintVariants[i] != null) Destroy(paintVariants[i]);
                paintVariants[i] = null;
            }
            paintVariants[0] = null; // alias of surfaces[0], not a second owner
            for (int i = 0; i < surfaces.Length; i++)
            {
                if (surfaces[i] != null) Destroy(surfaces[i]);
                surfaces[i] = null;
            }
        }
        // Android can report low memory during a heavy frame. Release pooled
        // scenery only from Update, never inside the notification callback.
        private void OnLowMemory() { lowMemoryPending = true; }

        private void Update()
        {
            if (lowMemoryPending)
            {
                lowMemoryPending = false;
                poolAllowedAt = Time.unscaledTime + 60f;
                // Only inactive cached rigs are released. Visible motorcycles
                // and all original source assets remain in the live scene.
                while (parkedBikePool.Count > 0)
                {
                    GameObject inactiveBike = parkedBikePool.Pop();
                    if (inactiveBike == null) continue;
                    bikeVisuals.Remove(inactiveBike);
                    Destroy(inactiveBike);
                }
            }
            if (player == null || !enabled) return;
            float now = Time.unscaledTime;
            if (now < nextPoll) return;
            nextPoll = now + .4f;
            Vector3 position = player.position;
            if (bikeSource == null)
            {
                if (bikePrewarm == null)
                {
                    // Do not deserialize the multi-mesh vehicle during cold boot,
                    // or when the player is flying far above every parking bay.
                    float warmDistanceSq = PrewarmRadius * PrewarmRadius;
                    foreach (var plan in plans)
                    {
                        Vector3 bay = new Vector3(plan.point.x, plan.point.y, plan.point.z);
                        if ((bay - position).sqrMagnitude > warmDistanceSq) continue;
                        bikePrewarm = AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(ModelResource);
                        if (bikePrewarm == null) return; // optional import deferred
                        break;
                    }
                    return;
                }
                if (!bikePrewarm.isDone) return;
#if UNITY_ANDROID && !UNITY_EDITOR
                // URP material setup allocates native resources too; spread it
                // away from optional vehicle and loot model integration.
                if (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()) return;
#endif
                bikeSource = bikePrewarm.asset as GameObject;
                bikePrewarm = null;
                if (bikeSource == null)
                {
                    Debug.LogWarning("Aaris bikes: motorcycle model missing.");
                    enabled = false;
                    return;
                }
#if UNITY_ANDROID && !UNITY_EDITOR
                if (AarisMobileStreamingFrameGate.IsConstrainedDevice())
                    bikeMaterialsBuild = StartCoroutine(BuildMaterialsAcrossFrames(true));
                else
                    SetupMaterials();
#else
                SetupMaterials();
#endif
                if (!enabled || !bikeMaterialsReady) return;
            }
            // A delayed import cannot expose incomplete URP surfaces on the
            // next 0.4 s poll. The 110 m predictive prewarm remains unchanged.
#if UNITY_ANDROID && !UNITY_EDITOR
            // Re-enable after a partially completed build: admit the first
            // slice again rather than leaving all roadside bikes invisible.
            if (!bikeMaterialsReady && bikeSource != null &&
                bikeMaterialsBuild == null &&
                AarisMobileStreamingFrameGate.IsConstrainedDevice() &&
                AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
                bikeMaterialsBuild = StartCoroutine(BuildMaterialsAcrossFrames(true));
#endif
            if (!bikeMaterialsReady) return;
            // Do not hold onto a completed Unity coroutine handle.
            bikeMaterialsBuild = null;
            int visibleCount = 0, unloaded = 0;
            foreach (var plan in plans)
            {
                if (plan.visible == null) continue;
                if ((plan.visible.transform.position - position).sqrMagnitude >
                    UnloadRadius * UnloadRadius && unloaded == 0)
                {
                    GameObject retired = plan.visible;
                    retired.SetActive(false);
                    // Invariant: visible + pooled never exceeds the original
                    // three-model cap; keep a defensive bound for scene edits.
                    if (Time.unscaledTime >= poolAllowedAt &&
                        parkedBikePool.Count < MaximumVisibleBikes)
                        parkedBikePool.Push(retired);
                    else
                    {
                        bikeVisuals.Remove(retired);
                        Destroy(retired);
                    }
                    plan.visible = null;
                    unloaded++;
                }
                else visibleCount++;
            }
            BikePlan nearest = null;
            float best = LoadRadius * LoadRadius;
            foreach (var plan in plans)
            {
                if (plan.visible != null || now < plan.nextProbe) continue;
                Vector3 point = new Vector3(plan.point.x, plan.point.y, plan.point.z);
                float d2 = (point - position).sqrMagnitude;
                if (d2 >= best) continue;
                best = d2;
                nearest = plan;
            }
            if (nearest == null) return;
            // Prefer a near-player parked motorcycle when all three slots are
            // full; an 8m hysteresis prevents distance-based thrashing.
            BikePlan parkedVictim = null;
            if (visibleCount >= MaximumVisibleBikes)
            {
                const float PriorityMarginMetres = 8f;
                float minVictimDistance = Mathf.Sqrt(best) + PriorityMarginMetres;
                float farthestDistanceSq = minVictimDistance * minVictimDistance;
                foreach (var plan in plans)
                {
                    if (plan.visible == null) continue;
                    float distanceSq =
                        (plan.visible.transform.position - position).sqrMagnitude;
                    if (distanceSq <= farthestDistanceSq) continue;
                    farthestDistanceSq = distanceSq;
                    parkedVictim = plan;
                }
                if (parkedVictim == null) return;
            }
            // The parked model stays until the new bay and real mesh validate.
            nearest.nextProbe = now + 3f;
            if (!TryParkingSpot(nearest.point, out Vector3 spot)) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            // Pooled-bike movement stays immediate. Gate only a brand-new
            // model, after validating floor and collision placement.
            if (parkedVictim == null && parkedBikePool.Count == 0 &&
                !AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())
            {
                nearest.nextProbe = now + .4f;
                return;
            }
#endif
            // A parking candidate is fully validated BEFORE moving any bike.
            // Reposition one of the same three original meshes with its
            // deterministic destination paint instead of Instantiate/Destroy.
            GameObject previous = parkedVictim != null ? parkedVictim.visible : null;
            GameObject candidate = parkedVictim != null
                ? ReuseBike(previous, nearest.point, spot)
                : parkedBikePool.Count > 0
                    ? ReuseBike(parkedBikePool.Pop(), nearest.point, spot)
                    : CreateBike(nearest.point, spot);
            if (candidate == null) return;
            if (parkedVictim != null)
            {
                parkedVictim.visible = null;
                parkedVictim.nextProbe = now + 3f;
            }
            nearest.visible = candidate;
        }

        private GameObject ReuseBike(GameObject root, AarisVehiclePoint point,
            Vector3 position)
        {
            if (root == null || !bikeVisuals.TryGetValue(root, out BikeVisualCache cache))
                return null;
            // Disabling first unregisters the BoxCollider. Reposition while
            // inactive, then enable at the already validated ground position.
            // No global Physics.SyncTransforms and no per-swap mesh import.
            root.SetActive(false);
            root.name = "Aaris parked motorcycle " + point.id;
            root.transform.SetPositionAndRotation(position,
                Quaternion.Euler(0f, point.yaw, 0f));
            int color = StablePaintIndex(point.id);
            for (int i = 0; i < cache.renderers.Length; i++)
            {
                Renderer renderer = cache.renderers[i];
                Material[] layout = cache.paintLayouts[color][i];
                if (renderer != null && layout != null)
                    renderer.sharedMaterials = layout;
            }
            root.SetActive(true);
            return root;
        }

        private bool TryParkingSpot(AarisVehiclePoint point, out Vector3 spot)
        {
            spot = default;
            Quaternion rotation = Quaternion.Euler(0f, point.yaw, 0f);
            Vector3 right = rotation * (Vector3.right * 3.8f);
            for (int side = 1; side >= -1; side -= 2)
            {
                Vector3 origin = new Vector3(point.x, point.y + 3f, point.z)
                    + right * side;
                int count = Physics.RaycastNonAlloc(origin, Vector3.down,
                    supportHits, 7f, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                RaycastHit[] floorHits = supportHits;
                if (count >= supportHits.Length)
                {
                    // Saturated NonAlloc results are unordered and incomplete.
                    // Do not hide a valid bike bay just because nearby roads,
                    // terrain seams or houses filled the small hit buffer.
                    count = Physics.RaycastNonAlloc(origin, Vector3.down,
                        denseSupportHits, 7f, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore);
                    floorHits = denseSupportHits;
                    if (count >= denseSupportHits.Length)
                    {
                        // Exceptional dense scene only: preserve the same slope
                        // and authored elevation checks on every actual hit.
                        floorHits = Physics.RaycastAll(origin, Vector3.down,
                            7f, Physics.DefaultRaycastLayers,
                            QueryTriggerInteraction.Ignore);
                        count = floorHits.Length;
                    }
                }
                float nearestError = 1.5f;
                Vector3 ground = default;
                bool found = false;
                for (int i = 0; i < count; i++)
                {
                    RaycastHit hit = floorHits[i];
                    if (hit.collider == null || hit.normal.y < .965f) continue;
                    float error = Mathf.Abs(hit.point.y - point.y);
                    if (error >= nearestError) continue;
                    nearestError = error;
                    ground = hit.point;
                    found = true;
                }
                if (!found) continue;
                Vector3 center = ground + Vector3.up * .79f;
                Vector3 half = new Vector3(.55f,.69f,1.21f);
                if (nearbyColliders != null &&
                    nearbyColliders.HasPotentialInactiveObstacle(center,half,rotation))
                    continue;
                int obstacles = Physics.OverlapBoxNonAlloc(center, half,
                    blockingHits, rotation, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                Collider[] foundColliders = blockingHits;
                if (obstacles >= blockingHits.Length)
                {
                    // Never accept a partial overlap set as clear; retry with
                    // preallocated storage before rare complete-query fallback.
                    obstacles = Physics.OverlapBoxNonAlloc(center, half,
                        denseBlockingHits, rotation, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore);
                    foundColliders = denseBlockingHits;
                    if (obstacles >= denseBlockingHits.Length)
                    {
                        foundColliders = Physics.OverlapBox(center, half, rotation,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                        obstacles = foundColliders.Length;
                    }
                }
                bool blocked = false;
                for (int i = 0; i < obstacles; i++)
                {
                    Collider collider = foundColliders[i];
                    if (collider != null && !(collider is TerrainCollider))
                    {
                        blocked = true;
                        break;
                    }
                }
                if (blocked) continue;
                spot = ground + Vector3.up * .035f;
                return true;
            }
            return false;
        }

        private static int SurfaceIndex(Material source, int fallback)
        {
            if (source == null) return fallback;
            string name = source.name.ToLowerInvariant();
            if (name.Contains("paint")) return 0;
            if (name.Contains("tyre") || name.Contains("rubber")) return 1;
            if (name.Contains("rim") || name.Contains("chrome")) return 2;
            if (name.Contains("engine")) return 3;
            if (name.Contains("seat")) return 4;
            if (name.Contains("headlamp")) return 5;
            if (name.Contains("taillamp")) return 6;
            if (name.Contains("amber")) return 7;
            return fallback;
        }

        private static int StablePaintIndex(string id)
        {
            // Deterministic across Android launches; unlike GetHashCode.
            if (string.IsNullOrEmpty(id)) return 0;
            uint hash = 2166136261u;
            unchecked
            {
                foreach (char c in id) hash = (hash ^ c) * 16777619u;
            }
            return (int)(hash % 4u);
        }

        private static bool HasVisibleBikeMesh(Renderer[] renderers)
        {
            if (renderers == null) return false;
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled ||
                    renderer.forceRenderingOff ||
                    !renderer.gameObject.activeInHierarchy) continue;
                if (renderer is MeshRenderer)
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null &&
                        filter.sharedMesh.vertexCount > 0) return true;
                }
                else if (renderer is SkinnedMeshRenderer skinned &&
                         skinned.sharedMesh != null &&
                         skinned.sharedMesh.vertexCount > 0) return true;
            }
            return false;
        }

        private GameObject CreateBike(AarisVehiclePoint point, Vector3 location)
        {
            var root = new GameObject("Aaris parked motorcycle " + point.id);
            root.transform.SetParent(transform, false);
            root.transform.SetPositionAndRotation(location,
                Quaternion.Euler(0f, point.yaw, 0f));
            GameObject imported = Instantiate(bikeSource, root.transform, false);
            imported.name = "Original sport motorcycle mesh";
            foreach (var sourceCollider in imported.GetComponentsInChildren<Collider>(true))
                sourceCollider.enabled = false;
            Renderer[] renderers = imported.GetComponentsInChildren<Renderer>(true);
            // Renderer count is not proof of a visible motorcycle. Guard
            // against disabled mesh renderers and zero-vertex imports before
            // adding an invisible obstacle to the player's road.
            if (!HasVisibleBikeMesh(renderers))
            {
                Destroy(root);
                return null;
            }
            // Static left-side kickstand pose; do not rotate or tilt the
            // physics root / parking test. Preserve importer axis correction.
            imported.transform.localRotation *= Quaternion.Euler(0f, 0f, 3.5f);
            // One shared URP material per authored surface, reused across
            // all visible bikes. No per-bike Material instances or per-frame GC.
            // This matches OBJ usemtl section order, NOT palette declaration
            // order: Tyre, Rim, Engine, Seat, Paint, Amber, Headlamp, TailLamp.
            int[] fallbackOrder = {1,2,3,4,0,7,5,6};
            int paintIndex = StablePaintIndex(point.id);
            var layouts = new Material[paintVariants.Length][][];
            for (int paint = 0; paint < paintVariants.Length; paint++)
                layouts[paint] = new Material[renderers.Length][];
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                Material[] source = renderer.sharedMaterials;
                if (source == null || source.Length == 0) continue;
                var kinds = new int[source.Length];
                for (int i = 0; i < source.Length; i++)
                    kinds[i] = SurfaceIndex(source[i],
                        fallbackOrder[Mathf.Min(i, fallbackOrder.Length - 1)]);
                for (int paint = 0; paint < paintVariants.Length; paint++)
                {
                    Material[] converted = new Material[source.Length];
                    for (int i = 0; i < source.Length; i++)
                    {
                        int kind = kinds[i];
                        converted[i] = kind == 0 ? paintVariants[paint] : surfaces[kind];
                    }
                    layouts[paint][r] = converted;
                }
                renderer.sharedMaterials = layouts[paintIndex][r];
#if UNITY_ANDROID && !UNITY_EDITOR
                // On low-RAM phones, eight motorcycle material slots do not
                // need eight additional realtime shadow-pass submissions.
                if (SystemInfo.systemMemorySize > 0 &&
                    SystemInfo.systemMemorySize <= 3072)
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
#endif
            }
            // Native renderer LOD is free of Update callbacks. Keep visible
            // motorbikes until they are tiny on screen rather than unloading
            // them abruptly at an arbitrary camera-relative distance.
            var lod = root.AddComponent<LODGroup>();
            lod.fadeMode = LODFadeMode.None;
            lod.animateCrossFading = false;
            lod.SetLODs(new[] { new LOD(.012f, renderers) });
            lod.RecalculateBounds();
            var collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, .76f, 0f);
            collider.size = new Vector3(.96f, 1.47f, 2.27f);
            bikeVisuals.Add(root, new BikeVisualCache {
                renderers = renderers, paintLayouts = layouts
            });
            return root;
        }

        private void OnDestroy()
        {
            foreach (Material material in surfaces)
                if (material != null) Destroy(material);
            // Paint variant zero aliases surfaces[0], so do not destroy twice.
            for (int i = 1; i < paintVariants.Length; i++)
                if (paintVariants[i] != null) Destroy(paintVariants[i]);
        }
    }
}
