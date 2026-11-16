using System;
using System.Collections.Generic;
using UnityEngine;

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
        public float nextProbe;
        public bool previouslySpawned;
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
        // Long enough to reveal parked cars before the player reaches them;
        // the 10-car active cap and 2GB Android spawn budget stay unchanged.
        private const float LoadRadius = 105f;
        private const float AuthoredPrewarmRadius = 150f;
        private const float UnloadRadius = 160f;
        private const float SeaLevel = -38.07552f;
        private const int TargetVehicles = 44;
        private const int MaximumNearbyVehicles = 10;
        private readonly List<AarisVehicleRecord> plans = new List<AarisVehicleRecord>(TargetVehicles);
        private readonly RaycastHit[] rayHits = new RaycastHit[24];
        private readonly Collider[] collisionHits = new Collider[40];
        private Transform player;
        private AarisNearbyColliders nearbyColliders;
        private float nextPoll;
        private uint randomState;
        private AarisVehicleType[] vehicleTypes;
        private GameObject[] prefabs;
        private GameObject[] wheelPrefabs;
        // High-detail authored bodies are prewarmed asynchronously only when
        // the player approaches a planned car. This preserves the exact same
        // visuals while avoiding five multi-megabyte FBX loads during boot.
        private ResourceRequest[] authoredBodyRequests;
        private bool[] authoredBodyLoadingFallback;
        private bool[] authoredBodyResolved;
        private int validModels;
        private Material paletteMaterial;
        private Material wheelPaletteMaterial;
        // Convert each imported source material once, then share it across all
        // streamed instances. This preserves PBR texture subassets without
        // per-car material duplication on low-memory Android devices.
        private readonly Dictionary<Material, Material> authoredMaterialCache =
            new Dictionary<Material, Material>();
        // Instantiate/Destroy of a complete car can spike a 2 GB Android frame.
        // Bound structural changes per 300 ms poll; retain all 44 authored plans.
        private int spawnBudgetPerPoll = 2;
        private int unloadBudgetPerPoll = 4;
        // Even a failed spawn has up to five ground rays + one box overlap.
        // Limit physics probes as well as successful Instantiate operations.
        private int spawnProbeBudgetPerPoll = 8;
        // Heavy authored FBXs can each occupy several megabytes once Unity
        // expands mesh/material data. Bound simultaneous async imports so
        // low-RAM phones never stack six vehicle bodies in one memory spike.
        private int maxConcurrentAuthoredLoads = 2;


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

        internal static bool TryGetVisualBoundsLocal(Transform localSpace, out Bounds combined)
        {
            return TryGetVisualBoundsLocal(localSpace, localSpace, out combined);
        }

        // Measure one imported subtree directly in another transform's axes.
        // Project source mesh bounds once, never a world-aligned bounding box.
        internal static bool TryGetVisualBoundsLocal(Transform localSpace,
            Transform subtree, out Bounds combined)
        {
            combined = new Bounds();
            if (localSpace == null || subtree == null) return false;
            bool found = false;
            Matrix4x4 toLocal = localSpace.worldToLocalMatrix;
            foreach (var filter in subtree.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter == null || filter.sharedMesh == null ||
                    filter.GetComponent<MeshRenderer>() == null) continue;
                AccumulateLocalBounds(filter.sharedMesh.bounds,
                    toLocal * filter.transform.localToWorldMatrix,
                    ref combined, ref found);
            }
            foreach (var skinned in subtree.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skinned == null || skinned.sharedMesh == null) continue;
                AccumulateLocalBounds(skinned.localBounds,
                    toLocal * skinned.transform.localToWorldMatrix,
                    ref combined, ref found);
            }
            return found;
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
            int memoryMB = SystemInfo.systemMemorySize;
            if (memoryMB > 0 && memoryMB <= 3072)
            {
                spawnBudgetPerPoll = 1;
                unloadBudgetPerPoll = 2;
                spawnProbeBudgetPerPoll = 3;
                maxConcurrentAuthoredLoads = 1;
            }
#endif
        }

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
            if (!world.Setup()) { Destroy(obj); return; }
            var session = motor.GetComponent<AarisVehicleSession>();
            if (session == null) session = motor.gameObject.AddComponent<AarisVehicleSession>();
            session.Configure(world, motor);
            var hudObject = new GameObject("Aaris Four Arrow Drive HUD");
            var hud = hudObject.AddComponent<AarisVehicleHUD>();
            hud.Configure(session);
        }

        private bool Setup()
        {
            // Read the saved static-collider streamer once, never per car/frame.
            nearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();
            TextAsset points = Resources.Load<TextAsset>("AarisVehicles/vehicle_candidates");
            TextAsset catalog = Resources.Load<TextAsset>("AarisVehicles/vehicle_catalog");
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
            authoredBodyRequests = new ResourceRequest[vehicleTypes.Length];
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
            paletteMaterial.SetFloat("_Metallic", .18f);
            paletteMaterial.SetFloat("_Smoothness", .55f);
            paletteMaterial.enableInstancing = true;

            wheelPaletteMaterial = new Material(shader) { name = "Aaris CC0 Kenney Wheel Rubber" };
            wheelPaletteMaterial.SetTexture("_BaseMap", palette);
            wheelPaletteMaterial.SetColor("_BaseColor", Color.white);
            wheelPaletteMaterial.SetFloat("_Metallic", 0f);
            wheelPaletteMaterial.SetFloat("_Smoothness", .18f);
            wheelPaletteMaterial.enableInstancing = true;

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

        private static Transform FindNamedWheel(Transform root, string token)
        {
            if (root == null) return null;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null &&
                    all[i].name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return all[i];
            return null;
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

        private static bool HasEmbeddedWheelSet(GameObject source)
        {
            if (source == null) return false;
            bool fl = false, fr = false, rl = false, rr = false;
            foreach (var t in source.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                string n = t.name.ToLowerInvariant();
                fl |= n.Contains("wheel_fl");
                fr |= n.Contains("wheel_fr");
                rl |= n.Contains("wheel_rl");
                rr |= n.Contains("wheel_rr");
            }
            return fl && fr && rl && rr;
        }

        private Material AuthoredUrpMaterial(Material source)
        {
            if (source == null) return paletteMaterial;
            if (source.shader != null &&
                source.shader.name == "Universal Render Pipeline/Lit")
                return source;
            if (authoredMaterialCache.TryGetValue(source, out Material cached) &&
                cached != null) return cached;
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
            Texture baseMap = source.HasProperty("_BaseMap")
                ? source.GetTexture("_BaseMap") :
                source.HasProperty("_MainTex") ? source.GetTexture("_MainTex") : null;
            if (result.HasProperty("_BaseColor")) result.SetColor("_BaseColor", baseColor);
            if (baseMap != null && result.HasProperty("_BaseMap"))
                result.SetTexture("_BaseMap", baseMap);

            Texture normal = source.HasProperty("_BumpMap")
                ? source.GetTexture("_BumpMap") : null;
            if (normal != null && result.HasProperty("_BumpMap"))
            {
                result.SetTexture("_BumpMap", normal);
                result.EnableKeyword("_NORMALMAP");
            }
            Texture metallicMap = source.HasProperty("_MetallicGlossMap")
                ? source.GetTexture("_MetallicGlossMap") : null;
            if (metallicMap != null && result.HasProperty("_MetallicGlossMap"))
            {
                result.SetTexture("_MetallicGlossMap", metallicMap);
                result.EnableKeyword("_METALLICSPECGLOSSMAP");
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
                label.Contains("tyre") || label.Contains("rubber");
            bool headlight = label.Contains("headlight");
            bool taillight = label.Contains("taillight") ||
                label.Contains("tail_light");
            if (glass)
            {
                baseColor.a = Mathf.Min(baseColor.a, .38f);
                if (result.HasProperty("_BaseColor"))
                    result.SetColor("_BaseColor", baseColor);
                if (result.HasProperty("_Metallic")) result.SetFloat("_Metallic", .02f);
                if (result.HasProperty("_Smoothness")) result.SetFloat("_Smoothness", .88f);
                result.SetOverrideTag("RenderType", "Transparent");
                if (result.HasProperty("_Surface")) result.SetFloat("_Surface", 1f);
                if (result.HasProperty("_SrcBlend"))
                    result.SetFloat("_SrcBlend",
                        (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (result.HasProperty("_DstBlend"))
                    result.SetFloat("_DstBlend",
                        (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (result.HasProperty("_ZWrite")) result.SetFloat("_ZWrite", 0f);
                result.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                result.renderQueue =
                    (int)UnityEngine.Rendering.RenderQueue.Transparent;
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
            else
            {
                // Painted steel: enough reflection to read body curvature but
                // not the mirror-like toy finish produced by smoothness=1.
                if (result.HasProperty("_Metallic")) result.SetFloat("_Metallic", .32f);
                if (result.HasProperty("_Smoothness")) result.SetFloat("_Smoothness", .58f);
            }
            authoredMaterialCache[source] = result;
            return result;
        }

        private void ApplyAuthoredMaterials(Renderer[] renderers)
        {
            foreach (var renderer in renderers)
            {
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
            var renderers = source.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return false;
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0) return false;
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

        private int TypeIndex(AarisVehicleType type)
        {
            if (type == null || vehicleTypes == null) return -1;
            for (int i = 0; i < vehicleTypes.Length; i++)
                if (ReferenceEquals(vehicleTypes[i], type)) return i;
            return -1;
        }

        private void CommitAuthoredBody(int i, GameObject authoredBody)
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
                // The sedan wheel asset is tiny compared with the authored body;
                // loading it only after the body is ready avoids boot-time churn.
                var authoredWheel =
                    Resources.Load<GameObject>(item.authoredWheelResource);
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
                var request = authoredBodyRequests[i];
                if (request == null || !request.isDone) continue;
                authoredBodyRequests[i] = null;
                var body = request.asset as GameObject;
                if (body != null &&
                    body.GetComponentInChildren<Renderer>(true) != null)
                {
                    CommitAuthoredBody(i, body);
                    authoredBodyResolved[i] = true;
                    continue;
                }
                var item = vehicleTypes[i];
                if (!authoredBodyLoadingFallback[i] &&
                    !string.IsNullOrEmpty(item.fallbackAuthoredModelResource))
                {
                    authoredBodyLoadingFallback[i] = true;
                    var fallback = Resources.LoadAsync<GameObject>(
                        item.fallbackAuthoredModelResource);
                    fallback.priority = -1;
                    authoredBodyRequests[i] = fallback;
                }
                else authoredBodyResolved[i] = true; // keep legacy fail-safe
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
            }
            if (inFlight >= maxConcurrentAuthoredLoads) return false;
            var item = vehicleTypes[i];
            if (string.IsNullOrEmpty(item.authoredModelResource))
            {
                authoredBodyResolved[i] = true;
                return false;
            }
            var request = Resources.LoadAsync<GameObject>(item.authoredModelResource);
            request.priority = -1;
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

        private static Terrain TerrainAt(float x, float z)
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null) continue;
                var p = terrain.transform.position;
                var size = terrain.terrainData.size;
                if (x >= p.x && x <= p.x + size.x &&
                    z >= p.z && z <= p.z + size.z) return terrain;
            }
            return null;
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
            var forward = angle * Vector3.forward * 1.42f;
            var right = angle * Vector3.right * .79f;
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
            Vector3 halfExtents = new Vector3(1.14f, .64f, 2.35f);
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

        private bool Build(AarisVehicleRecord record, Vector3 pos)
        {
            int typeIndex = TypeIndex(record.type);
            if (typeIndex < 0 || !authoredBodyResolved[typeIndex]) return false;
            GameObject source = prefabs[typeIndex];
            GameObject wheelSource = wheelPrefabs[typeIndex];
            if (source == null || wheelSource == null) return false;
            var root = new GameObject("Aaris Vehicle " + record.type.id + " " + record.point.id);
            root.transform.SetParent(transform, false);
            root.transform.SetPositionAndRotation(pos, record.rotation);
            var mesh = Instantiate(source, root.transform, false);
            mesh.name = record.type.preserveAuthoredUrpMaterials ||
                record.type.useEmbeddedAuthoredWheels ||
                record.type.convertAuthoredBodyMaterials
                ? "Authored Vehicle Body" : "Local CC0 Car Model";
            foreach (var collider in mesh.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            var renderers = mesh.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) { Destroy(root); return false; }
            // Kenney Car Kit uses one UV palette atlas. Share ONE URP Lit
            // material across streamed instances; never instantiate new
            // materials per car/per frame.
            if (record.type.useEmbeddedAuthoredWheels ||
                record.type.convertAuthoredBodyMaterials)
            {
                ApplyAuthoredMaterials(renderers);
            }
            else if (!record.type.preserveAuthoredUrpMaterials)
            {
                foreach (var renderer in renderers)
                {
                    int submeshes = VisualSubmeshCount(renderer);
                    Material[] materials = new Material[submeshes];
                    for (int i = 0; i < submeshes; i++) materials[i] = paletteMaterial;
                    renderer.sharedMaterials = materials;
                }
            }
            // Inspect the model in chassis space; world-space AABBs change
            // when the authored road marker is rotated 45/90/135 degrees.
            if (!TryGetVisualBoundsLocal(root.transform, out Bounds bounds))
            { Destroy(root); return false; }
            if (bounds.size.x > bounds.size.z * 1.1f)
                mesh.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            if (record.type.useEmbeddedAuthoredWheels)
                FaceEmbeddedVehicleForward(root.transform, mesh.transform);
            if (!TryGetVisualBoundsLocal(root.transform, out bounds) ||
                bounds.size.z < .1f || bounds.size.x < .1f)
            { Destroy(root); return false; }
            float scale = Mathf.Min(1.88f / bounds.size.x, 4.28f / bounds.size.z);
            mesh.transform.localScale *= Mathf.Clamp(scale, .01f, 50f);
            if (!TryGetVisualBoundsLocal(root.transform, out bounds))
            { Destroy(root); return false; }
            // Both this offset and bounds are expressed in the root's LOCAL
            // frame: chassis collider, FBX art and WheelCollider heading agree.
            mesh.transform.localPosition += new Vector3(0f, .48f, 0f) - bounds.center;
            var motor = root.AddComponent<AarisVehicleMotor>();
            motor.Configure(record.type.id, record.type.maxSpeedMetres, record.type.massKg);
            // After the mesh is recentered to local Y=.48, its size remains
            // valid in chassis space at every road yaw. Align physical shell
            // and wheels to the visible type instead of a generic sedan.
            motor.FitShellAndWheelbase(new Bounds(new Vector3(0f, .48f, 0f),
                bounds.size));
            bool wheelsAttached = record.type.useEmbeddedAuthoredWheels
                ? motor.AttachEmbeddedWheelModels(mesh.transform)
                : motor.AttachWheelModels(wheelSource, wheelPaletteMaterial,
                    record.type.preserveAuthoredUrpMaterials);
            if (!wheelsAttached)
            {
                Destroy(root);
                return false;
            }
            record.visible = motor;
            if (Debug.isDebugBuild)
                Debug.Log("AARIS_CAR_PLACED " + record.point.id + " " +
                    record.type.id + " " + root.transform.position.ToString("F2"));
            return true;
        }

        private void Update()
        {
            if (player == null) return;
            float now = Time.unscaledTime;
            if (now < nextPoll) return;
            nextPoll = now + .3f;
            // One native Transform read per poll, not one per vehicle record.
            Vector3 playerPosition = player.position;
            int active = 0, built = 0, unloaded = 0;
            int spawnProbes = 0;
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
                    Destroy(record.visible.gameObject);
                    record.visible = null;
                    unloaded++;
                }
                else active++;
            }
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
            if (closestType >= 0) StartAuthoredBodyPrewarm(closestType);

            foreach (var record in plans)
            {
                if (active >= MaximumNearbyVehicles ||
                    built >= spawnBudgetPerPoll ||
                    spawnProbes >= spawnProbeBudgetPerPoll) break;
                if (record.visible != null || now < record.nextProbe) continue;
                if ((record.position - playerPosition).sqrMagnitude > LoadRadius * LoadRadius)
                    continue;
                int typeIndex = TypeIndex(record.type);
                if (typeIndex >= 0 && !authoredBodyResolved[typeIndex]) continue;
                spawnProbes++;
                if (TryResolvePlanned(record, out Vector3 pos) && Build(record, pos))
                {
                    active++;
                    built++;
                    record.position = pos;
                    record.previouslySpawned = true;
                }
                else
                {
                    record.nextProbe = now + 3.0f;
                }
            }
        }

        private void OnDestroy()
        {
            if (paletteMaterial != null) Destroy(paletteMaterial);
            if (wheelPaletteMaterial != null) Destroy(wheelPaletteMaterial);
        }

        public AarisVehicleMotor NearestAvailable(Vector3 origin, float maxDistance)
        {
            AarisVehicleMotor nearest = null;
            float best = maxDistance * maxDistance;
            foreach (var record in plans)
            {
                if (record.visible == null || record.visible.IsOccupied) continue;
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
