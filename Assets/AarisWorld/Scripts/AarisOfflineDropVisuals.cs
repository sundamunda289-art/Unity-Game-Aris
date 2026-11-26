using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;

namespace Aaris.MapReview
{
    /// <summary>
    /// Original, fully local single-player aircraft/canopy geometry. No URLs, CDN, Asset Store
    /// requests or runtime downloads. Optional local Resources models may replace these.
    /// </summary>
    internal static class AarisOfflineDropVisuals
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static readonly ProfilerMarker k_PlaneCreate =
            new ProfilerMarker("Aaris.Startup.PlaneCreate");
#endif
        private static Material hull, dark, wingPanels, engineDark, safetyAccent,
            propellerBlur, wingtipCondensation, navRed, navGreen, navWhite,
            canopyBlue, canopyWhite, canopyOrange, rope;
        // Unity may normalize serialized _EMISSION keywords while importing materials.
        // One runtime-only copy per aircraft finish keeps the saved assets untouched.
        private static Material hullDaylightFinish, wingDaylightFinish;
        private static Mesh cachedTwinPropellerMesh, cachedTransportPropellerMesh;
        private static Mesh cachedTwinContrailMesh, cachedTransportContrailMesh;
        private static readonly Mesh[] cachedPrimitiveMeshes = new Mesh[6];
        // Same Unity built-in cylinder mesh as CreatePrimitive, but cached
        // once so parachute ropes no longer manufacture throwaway colliders.
        private static Mesh cachedCanopyCordMesh;
#if UNITY_ANDROID && !UNITY_EDITOR
        private static GameObject prewarmedCanopyPrefab;
        private static bool canopyVisualsPrewarmed;
        // Five authored PBR finishes were previously loaded synchronously
        // on the same frame as the high-detail plane Instantiate. Prefetch
        // their exact Resources assets across frames before that expensive
        // native hierarchy integration, without changing any material data.
        private static readonly string[] AircraftFinishNames = {
            "CargoPlane-HulledMetal", "CargoPlane-DarkCockpit",
            "CargoPlane-WingPanels", "CargoPlane-EngineDark",
            "CargoPlane-SafetyAccent"
        };
        private static readonly Dictionary<string, Material> aircraftFinishCache =
            new Dictionary<string, Material>(5, StringComparer.Ordinal);
        private static bool aircraftFinishesPrewarmed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCanopyPrewarm()
        {
            // Static references must not outlive a Unity player domain reload.
            prewarmedCanopyPrefab = null;
            canopyVisualsPrewarmed = false;
            cachedCanopyCordMesh = null;
            aircraftFinishCache.Clear();
            aircraftFinishesPrewarmed = false;
        }

        internal static IEnumerator PrewarmAircraftFinishes()
        {
            if (aircraftFinishesPrewarmed) yield break;
            for (int i = 0; i < AircraftFinishNames.Length; i++)
            {
                string name = AircraftFinishNames[i];
                // One low-priority native import in flight at a time. Never
                // construct a new Material or modify authored shader keywords.
                ResourceRequest request = Resources.LoadAsync<Material>(
                    "AarisDrop/Materials/" + name);
                request.priority = -1;
                yield return request;
                aircraftFinishCache[name] = request.asset as Material;
                yield return null;
            }
            aircraftFinishesPrewarmed = true;
        }

        internal static IEnumerator PrewarmCanopyPrefab()
        {
            // The cargo aircraft and essential flight input render first.
            // Keep all art identical; stage the first canopy's material setup
            // and one built-in rope mesh away from the OPEN gameplay frame.
            yield return null;
            if (prewarmedCanopyPrefab == null)
            {
                ResourceRequest request = Resources.LoadAsync<GameObject>(
                    "AarisDrop/ElongatedParachute");
                request.priority = -1;
                yield return request;
                prewarmedCanopyPrefab = request.asset as GameObject;
            }
            if (canopyVisualsPrewarmed) yield break;
            // Each potentially expensive Shader.Find/Material allocation gets
            // its own later flight frame, not the initial aircraft renderer.
            yield return null;
            Paint(ref canopyBlue, new Color(0.05f, 0.23f, 0.39f));
            yield return null;
            Paint(ref canopyWhite, new Color(0.91f, 0.91f, 0.83f));
            yield return null;
            Paint(ref canopyOrange, new Color(0.97f, 0.45f, 0.13f));
            yield return null;
            Paint(ref rope, new Color(0.09f, 0.12f, 0.15f));
            yield return null;
            EnsureCanopyCordMesh();
            canopyVisualsPrewarmed = true;
        }
#endif
        // One-time cold-start timings. Resolve, instantiate and material setup
        // have different mitigation strategies; do not guess from APK size.
        internal static float PlaneModelResolveMs { get; private set; } = -1f;
        internal static float PlaneInstantiateMs { get; private set; } = -1f;
        internal static float PlaneTotalCreationMs { get; private set; } = -1f;
        internal static bool PlaneHighDetailSelected { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetPlaneTimings()
        {
            PlaneModelResolveMs = PlaneInstantiateMs = PlaneTotalCreationMs = -1f;
            PlaneHighDetailSelected = false;
        }

        private static Material Paint(ref Material cached, Color color)
        {
            if (cached != null) return cached;
            Shader shader = AarisShaderCache.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = AarisShaderCache.Find("Universal Render Pipeline/Simple Lit");
            if (shader == null) shader = AarisShaderCache.Find("Standard");
            if (shader == null) shader = AarisShaderCache.Find("Sprites/Default");
            if (shader == null) shader = AarisShaderCache.Find("Hidden/InternalErrorShader");
            if (shader == null)
                throw new InvalidOperationException("AARIS_OFFLINE_URP_SHADER_UNAVAILABLE");
            cached = new Material(shader);
            cached.name = "AarisDrop_Original_" + ColorUtility.ToHtmlStringRGB(color);
            if (cached.HasProperty("_BaseColor")) cached.SetColor("_BaseColor", color);
            else cached.color = color;
            cached.enableInstancing = true;
            return cached;
        }

        // Tiny aircraft marker lights and high-RPM propeller blur should
        // remain readable against sky without spawning real Light components.
        // One shared unlit material per colour keeps mobile fill/draw cost small.
        private static Material Unlit(ref Material cached, Color color,
            bool transparent = false)
        {
            if (cached != null) return cached;
            Shader shader = AarisShaderCache.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = AarisShaderCache.Find("Sprites/Default");
            if (shader == null) shader = AarisShaderCache.Find("Unlit/Color");
            if (shader == null) return Paint(ref cached, color);
            cached = new Material(shader)
            {
                name = "AarisDrop_Unlit_" + ColorUtility.ToHtmlStringRGBA(color)
            };
            if (cached.HasProperty("_BaseColor")) cached.SetColor("_BaseColor", color);
            else cached.color = color;
            if (transparent)
            {
                cached.SetOverrideTag("RenderType", "Transparent");
                if (cached.HasProperty("_Surface")) cached.SetFloat("_Surface", 1f);
                if (cached.HasProperty("_SrcBlend"))
                    cached.SetFloat("_SrcBlend",
                        (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (cached.HasProperty("_DstBlend"))
                    cached.SetFloat("_DstBlend",
                        (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (cached.HasProperty("_ZWrite")) cached.SetFloat("_ZWrite", 0f);
                cached.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                cached.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            cached.enableInstancing = true;
            return cached;
        }

        // PBR material values are cached once, not allocated or updated per frame.
        // Distinguish painted metal, rubber-dark engine parts and glazed cockpit
        // without adding texture samples, normal maps or extra draw calls.
        private static void SetAircraftFinish(Material material, float metallic, float smoothness)
        {
            if (material == null) return;
            if (material.HasProperty("_Metallic"))
                material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness"))
                material.SetFloat("_Smoothness", smoothness);
        }

        // Resolve once per aircraft spawn. Never mutate the shared saved URP
        // materials; old/mobile imports without these assets keep the original
        // lightweight cached fallback and its independent finish settings.
        private static Material SavedAircraftFinish(string materialName,
            ref Material fallback, Color fallbackColor, float metallic, float smoothness)
        {
            Material saved;
#if UNITY_ANDROID && !UNITY_EDITOR
            // Android flight prewarm resolves all five entries first. Keep
            // the original sync fallback for an unexpected immediate spawn.
            if (!aircraftFinishesPrewarmed ||
                !aircraftFinishCache.TryGetValue(materialName, out saved))
                saved = Resources.Load<Material>("AarisDrop/Materials/" + materialName);
#else
            saved = Resources.Load<Material>("AarisDrop/Materials/" + materialName);
#endif
            if (saved != null && saved.shader != null &&
                saved.shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
                return saved;
            Material material = Paint(ref fallback, fallbackColor);
            SetAircraftFinish(material, metallic, smoothness);
            return material;
        }

        // On Unity 6, importing saved URP materials can remove _EMISSION from
        // m_ValidKeywords while retaining their authored _EmissionColor. Without
        // that keyword the aircraft can read as a black silhouette in daylight.
        // Clone exactly once per finish: no per-frame material writes, added
        // lights, texture samples or mutation of shared Resources assets.
        private static Material EnsureDaylightFill(Material source, ref Material cached)
        {
            if (source == null || !source.HasProperty("_EmissionColor")) return source;
            if (cached != null) return cached;
            cached = new Material(source)
            {
                name = source.name + " (Aaris runtime daylight fill)",
                enableInstancing = true
            };
            Color fill = cached.GetColor("_EmissionColor");
            if (fill.maxColorComponent < 0.05f)
                fill = new Color(0.095f, 0.106f, 0.103f, 1f);
            cached.SetColor("_EmissionColor", fill);
            cached.EnableKeyword("_EMISSION");
            return cached;
        }

        private static GameObject Part(Transform parent, string name, PrimitiveType primitive,
            Vector3 position, Quaternion rotation, Vector3 scale, Material material)
        {
            // Only the first inactive prototype owns a temporary collider.
            // Every actual aircraft part is constructed render-only.
            int slot = (int)primitive;
            if (cachedPrimitiveMeshes[slot] == null)
            {
                GameObject prototype = GameObject.CreatePrimitive(primitive);
                prototype.SetActive(false);
                cachedPrimitiveMeshes[slot] = prototype.GetComponent<MeshFilter>().sharedMesh;
                DisposeVisualObject(prototype);
            }
            GameObject obj = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = position;
            obj.transform.localRotation = rotation;
            obj.transform.localScale = scale;
            obj.GetComponent<MeshFilter>().sharedMesh = cachedPrimitiveMeshes[slot];
            obj.GetComponent<Renderer>().sharedMaterial = material;
            return obj;
        }

        private static void DisposeVisualObject(UnityEngine.Object value)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(value);
            else UnityEngine.Object.Destroy(value);
#else
            UnityEngine.Object.Destroy(value);
#endif
        }

        private static Mesh EnsureCanopyCordMesh()
        {
            if (cachedCanopyCordMesh != null) return cachedCanopyCordMesh;
            // Get Unity's EXACT original cylinder mesh once. The temporary
            // prototype is inactive before any collider can enter physics.
            // Rope instances below are render-only, as the old colliders were
            // destroyed immediately after CreatePrimitive anyway.
            GameObject prototype = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            prototype.SetActive(false);
            cachedCanopyCordMesh = prototype.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(prototype);
            return cachedCanopyCordMesh;
        }

        private static void RigidLine(Transform parent, string name, Vector3 a, Vector3 b,
            float diameter, Material material)
        {
            Vector3 offset = b - a;
            if (offset.sqrMagnitude < 0.0001f) return;
            // A regular cylinder primitive creates a Collider for every
            // suspension rope, then schedules every Collider for Destroy.
            // Use the same native mesh with no temporary physics components.
            GameObject line = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            line.transform.SetParent(parent, false);
            line.transform.localPosition = (a + b) * 0.5f;
            line.transform.localRotation = Quaternion.FromToRotation(Vector3.up, offset);
            line.transform.localScale =
                new Vector3(diameter, offset.magnitude * 0.5f, diameter);
            line.GetComponent<MeshFilter>().sharedMesh = EnsureCanopyCordMesh();
            line.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        // A single static mesh draws all engines. Analytic blades, the high-RPM
        // exposure disc and sun glint run in the retained GPU shader. There are
        // no animated rotor Transforms and no duplicated back-face triangles.
        private static Mesh BuildPropellerMesh(bool twin, float[] offsets,
            float rotorY, float rotorZ, float scale)
        {
            Mesh cached = twin ? cachedTwinPropellerMesh : cachedTransportPropellerMesh;
            if (cached != null) return cached;
            const int segments = 24;
            int stride = segments + 2;
            var vertices = new Vector3[offsets.Length * stride];
            var normals = new Vector3[vertices.Length];
            var uv = new Vector2[vertices.Length];
            var phase = new Vector2[vertices.Length];
            var triangles = new int[offsets.Length * segments * 3];
            for (int engine = 0; engine < offsets.Length; engine++)
            {
                int first = engine * stride;
                Vector3 center = new Vector3(offsets[engine], rotorY, rotorZ + .06f);
                vertices[first] = center;
                normals[first] = Vector3.forward;
                phase[first] = new Vector2((engine & 1) == 0 ? 1f : -1f, engine * 1.73f);
                for (int i = 0; i <= segments; i++)
                {
                    float angle = i * Mathf.PI * 2f / segments;
                    Vector2 circle = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                    int vertex = first + 1 + i;
                    vertices[vertex] = center + new Vector3(circle.x, circle.y, 0f) * (1.66f * scale);
                    uv[vertex] = circle;
                    normals[vertex] = Vector3.forward;
                    phase[vertex] = phase[first];
                    if (i == segments) continue;
                    int triangle = (engine * segments + i) * 3;
                    triangles[triangle] = first;
                    triangles[triangle + 1] = vertex;
                    triangles[triangle + 2] = vertex + 1;
                }
            }
            var mesh = new Mesh { name = twin ? "AarisTwinGpuPropellers" : "AarisTransportGpuPropellers" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uv;
            mesh.uv2 = phase;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            if (twin) cachedTwinPropellerMesh = mesh;
            else cachedTransportPropellerMesh = mesh;
            return mesh;
        }

        private static Material AircraftEffect(ref Material cached, string resource, string label)
        {
            if (cached != null) return cached;
            // Resources retention is essential: Shader.Find alone can succeed
            // in the Editor while the Android build strips an unreferenced shader.
            Shader shader = Resources.Load<Shader>(resource);
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("AARIS_AIRCRAFT_EFFECT_SHADER_UNAVAILABLE " + resource);
            cached = new Material(shader) { name = label, enableInstancing = true };
            return cached;
        }

        private static void ConfigureAircraftEffectRenderer(MeshRenderer renderer)
        {
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        }

        // Two crossed, tapered ribbons per wingtip: sixteen triangles TOTAL.
        // A short aerodynamic condensation wake follows the airframe without
        // particle simulation, TrailRenderer history or per-frame mesh writes.
        private static Mesh BuildWingtipCondensationMesh(bool twin)
        {
            Mesh cached = twin ? cachedTwinContrailMesh : cachedTransportContrailMesh;
            if (cached != null) return cached;
            var vertices = new Vector3[24];
            var uv = new Vector2[24];
            var triangles = new int[48];
            float span = twin ? 12.29f : 18.8f;
            float height = twin ? .54f : 2.68f;
            float trailingEdge = twin ? .36f : -2.41f;
            int index = 0;
            for (int side = 0; side < 2; side++)
            for (int ribbon = 0; ribbon < 2; ribbon++)
            {
                int first = (side * 2 + ribbon) * 6;
                Vector3 axis = ribbon == 0 ? Vector3.right : Vector3.up;
                for (int row = 0; row < 3; row++)
                {
                    float along = row == 0 ? 0f : row == 1 ? .3f : 1f;
                    Vector3 center = new Vector3((side == 0 ? -1f : 1f) * span,
                        height - along * .18f, trailingEdge - along * 18f);
                    float width = Mathf.Lerp(.035f, .24f, along);
                    vertices[first + row * 2] = center - axis * width;
                    vertices[first + row * 2 + 1] = center + axis * width;
                    uv[first + row * 2] = new Vector2(-1f, along);
                    uv[first + row * 2 + 1] = new Vector2(1f, along);
                    if (row == 2) continue;
                    int a = first + row * 2;
                    triangles[index++] = a; triangles[index++] = a + 1; triangles[index++] = a + 2;
                    triangles[index++] = a + 2; triangles[index++] = a + 1; triangles[index++] = a + 3;
                }
            }
            var mesh = new Mesh { name = twin ? "AarisTwinWingtipWake" : "AarisTransportWingtipWake" };
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            if (twin) cachedTwinContrailMesh = mesh;
            else cachedTransportContrailMesh = mesh;
            return mesh;
        }

        private static void AddWingtipCondensation(Transform root, bool twin)
        {
            var wake = new GameObject("WingtipCondensation", typeof(MeshFilter), typeof(MeshRenderer));
            wake.transform.SetParent(root, false);
            wake.GetComponent<MeshFilter>().sharedMesh = BuildWingtipCondensationMesh(twin);
            var renderer = wake.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = AircraftEffect(ref wingtipCondensation,
                "AarisWingtipCondensation", "Aaris shared wingtip condensation");
            ConfigureAircraftEffectRenderer(renderer);
        }

        private static void EnforceRenderOnlyAircraft(GameObject root)
        {
            // Imported OBJ assets currently contain neither component. Keep
            // future prefab replacements from silently enrolling a flying
            // visual in PhysX. Disable immediately before deferred destruction.
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
                DisposeVisualObject(collider);
            }
            foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true))
            {
                body.detectCollisions = false;
                body.useGravity = false;
                body.isKinematic = true;
                DisposeVisualObject(body);
            }
        }

        private static void AddAircraftMarkerLights(Transform root)
        {
            if (root == null) return;
            Material red = Unlit(ref navRed, new Color(1f, .035f, .02f, 1f));
            Material green = Unlit(ref navGreen, new Color(.02f, 1f, .16f, 1f));
            Material white = Unlit(ref navWhite, new Color(1f, .96f, .82f, 1f));
            // ICAO-style steady navigation markers: port red, starboard green,
            // aft white plus a small red anti-collision beacon. They are
            // emissive-looking unlit meshes, NOT dynamic lights.
            Part(root, "PortNavigationRed", PrimitiveType.Sphere,
                new Vector3(-12.55f, 1.38f, 1.15f), Quaternion.identity,
                Vector3.one * .23f, red);
            Part(root, "StarboardNavigationGreen", PrimitiveType.Sphere,
                new Vector3(12.55f, 1.38f, 1.15f), Quaternion.identity,
                Vector3.one * .23f, green);
            Part(root, "TailNavigationWhite", PrimitiveType.Sphere,
                new Vector3(0f, 2.15f, -10.15f), Quaternion.identity,
                Vector3.one * .20f, white);
            Part(root, "AntiCollisionBeacon", PrimitiveType.Sphere,
                new Vector3(0f, 3.0f, -1.0f), Quaternion.identity,
                Vector3.one * .18f, red);
        }

        internal static GameObject CreatePlane(Vector3 position, Quaternion rotation,
            out Transform[] propellers, GameObject preloadedPrefab = null,
            bool preloadedHighDetail = false, bool preloadedResolved = false)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var profilerScope = k_PlaneCreate.Auto();
#endif
            // Three cold-start clocks isolate Resources resolution vs native
            // prefab creation vs visual setup. Time is measured only once at
            // aircraft creation; never in LateUpdate/rotor animation.
            double totalStartedAt = Time.realtimeSinceStartupAsDouble;
            double lookupStartedAt = totalStartedAt;
            // Prefer the vendored CC0 flight-state twin turboprop. Keep the
            // original lightweight transport as an offline/mobile fallback.
            var prefab = preloadedPrefab;
            bool highDetailTwin = prefab != null;
            if (!preloadedResolved)
            {
                // Editor/desktop keep immediate loading for scene preview.
                prefab = Resources.Load<GameObject>("AarisDrop/CargoPlaneRealistic");
                highDetailTwin = prefab != null;
                if (!highDetailTwin)
                    prefab = Resources.Load<GameObject>("AarisDrop/CargoPlane");
            }
            else highDetailTwin = preloadedHighDetail && prefab != null;
            // Async disk IO was completed in earlier frames, not in this timer.
            PlaneModelResolveMs = preloadedResolved ? -1f :
                (float)((Time.realtimeSinceStartupAsDouble - lookupStartedAt) * 1000d);
            PlaneHighDetailSelected = highDetailTwin;
            GameObject root;
            if (prefab != null)
            {
                double instantiateStartedAt = Time.realtimeSinceStartupAsDouble;
                root = UnityEngine.Object.Instantiate(prefab, position, rotation);
                PlaneInstantiateMs = (float)((Time.realtimeSinceStartupAsDouble -
                    instantiateStartedAt) * 1000d);
                // Prefer the five serialized URP/Lit aircraft finishes. Unlike
                // flat procedural paints, these preserve authored PBR surface
                // properties and any future texture maps without per-frame work.
                Material hullPaint = SavedAircraftFinish("CargoPlane-HulledMetal",
                    ref hull, new Color(.31f, .36f, .35f), .38f, .32f);
                hullPaint = EnsureDaylightFill(hullPaint, ref hullDaylightFinish);
                Material cockpitPaint = SavedAircraftFinish("CargoPlane-DarkCockpit",
                    ref dark, new Color(.055f, .10f, .13f), .08f, .82f);
                Material wingPaint = SavedAircraftFinish("CargoPlane-WingPanels",
                    ref wingPanels, new Color(.39f, .43f, .40f), .30f, .25f);
                wingPaint = EnsureDaylightFill(wingPaint, ref wingDaylightFinish);
                Material enginePaint = SavedAircraftFinish("CargoPlane-EngineDark",
                    ref engineDark, new Color(.095f, .12f, .13f), .52f, .26f);
                Material safetyPaint = SavedAircraftFinish("CargoPlane-SafetyAccent",
                    ref safetyAccent, new Color(.60f, .60f, .49f), .18f, .27f);
                // Imported OBJ/MTL materials are not guaranteed to be URP-compatible.
                // Map each authored submesh to one cached, shared URP material.
                // Preserve wing/cockpit/intake contrast instead of flattening
                // the entire aircraft to one cartoon-like hull colour.
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var slots = renderer.sharedMaterials;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        string label = slots[i] != null ? slots[i].name : "";
                        if (label.IndexOf("Cockpit", StringComparison.OrdinalIgnoreCase) >= 0)
                            slots[i] = cockpitPaint;
                        else if (label.IndexOf("WingPanels", StringComparison.OrdinalIgnoreCase) >= 0)
                            slots[i] = wingPaint;
                        else if (label.IndexOf("EngineDark", StringComparison.OrdinalIgnoreCase) >= 0)
                            slots[i] = enginePaint;
                        else if (label.IndexOf("SafetyAccent", StringComparison.OrdinalIgnoreCase) >= 0)
                            slots[i] = safetyPaint;
                        // A stripped OBJ import can discard MTL slot names. The
                        // CC0 CargoPlaneRealistic.obj declares its first five
                        // materials in THIS order: WingPanels, EngineDark,
                        // HulledMetal, DarkCockpit, SafetyAccent. Keep the
                        // broad wing from turning engine-black on Android.
                        else if (slots.Length == 5)
                            slots[i] = i == 0 ? wingPaint :
                                i == 1 ? enginePaint :
                                i == 2 ? hullPaint :
                                i == 3 ? cockpitPaint : safetyPaint;
                        else slots[i] = hullPaint;
                    }
                    renderer.sharedMaterials = slots;
                }
            }
            else
            {
                PlaneInstantiateMs = -1f; // No imported model to instantiate.
                Debug.LogError("AARIS_CARGO_PLANE_MODEL_NOT_IMPORTED: using emergency offline placeholder.");
                root = new GameObject("Aaris_OriginalCargoAircraft");
                root.transform.SetPositionAndRotation(position, rotation);
                Material shell = Paint(ref hull, new Color(0.36f, 0.44f, 0.47f));
                Material trim = Paint(ref dark, new Color(0.09f, 0.14f, 0.17f));
                SetAircraftFinish(shell, .38f, .32f);
                SetAircraftFinish(trim, .08f, .82f);
                Part(root.transform, "Fuselage", PrimitiveType.Capsule, Vector3.zero,
                    Quaternion.Euler(90f, 0f, 0f),
                    new Vector3(3.1f, 10.6f, 3.1f), shell);
                Part(root.transform, "Nose", PrimitiveType.Sphere,
                    new Vector3(0f, -0.1f, 8.7f), Quaternion.identity,
                    new Vector3(2.95f, 2.9f, 4.4f), shell);
                Part(root.transform, "Cockpit", PrimitiveType.Sphere,
                    new Vector3(0f, 1.0f, 7.1f), Quaternion.identity,
                    new Vector3(2.2f, 0.92f, 2.45f), trim);
                Part(root.transform, "MainWing", PrimitiveType.Cube,
                    new Vector3(0f, 1.35f, 1.4f), Quaternion.identity,
                    new Vector3(26f, 0.34f, 4.2f), shell);
                Part(root.transform, "TailWing", PrimitiveType.Cube,
                    new Vector3(0f, 1.1f, -8.7f), Quaternion.identity,
                    new Vector3(10f, 0.28f, 2.65f), shell);
                Part(root.transform, "TailFin", PrimitiveType.Cube,
                    new Vector3(0f, 3.3f, -8.7f), Quaternion.Euler(-10f, 0f, 0f),
                    new Vector3(0.27f, 4.6f, 2.9f), shell);
                float[] xs = { -8.7f, -4.6f, 4.6f, 8.7f };
                for (int i = 0; i < xs.Length; i++)
                {
                    Part(root.transform, "EngineNacelle" + i, PrimitiveType.Capsule,
                        new Vector3(xs[i], 0.6f, 1.7f), Quaternion.Euler(90f, 0f, 0f),
                        new Vector3(1.5f, 1.8f, 1.5f), trim);
                }
            }

            // Preserve the authored engine locations and rotor diameter.
            float[] offsets = highDetailTwin
                ? new float[] { -4f, 4f }
                : new float[] { -8.65f, -4.55f, 4.55f, 8.65f };
            float rotorY = highDetailTwin ? .085f : .46f;
            float rotorZ = highDetailTwin ? 3.73f : 4.37f;
            float rotorScale = highDetailTwin ? 1.17f : 1f;
            GameObject rotors = new GameObject("GpuPropellerDiscs", typeof(MeshFilter), typeof(MeshRenderer));
            rotors.transform.SetParent(root.transform, false);
            rotors.GetComponent<MeshFilter>().sharedMesh = BuildPropellerMesh(
                highDetailTwin, offsets, rotorY, rotorZ, rotorScale);
            var rotorRenderer = rotors.GetComponent<MeshRenderer>();
            rotorRenderer.sharedMaterial = AircraftEffect(ref propellerBlur,
                "AarisAircraftPropeller", "Aaris shared GPU propeller exposure");
            ConfigureAircraftEffectRenderer(rotorRenderer);
            // Retain the creation API for drop startup. This is one static
            // combined renderer transform, never a CPU rotor-animation list.
            propellers = new[] { rotors.transform };
            AddWingtipCondensation(root.transform, highDetailTwin);
            AddAircraftMarkerLights(root.transform);
            EnforceRenderOnlyAircraft(root);
            root.name = "Aaris_CargoPlane";
            PlaneTotalCreationMs = (float)((Time.realtimeSinceStartupAsDouble -
                totalStartedAt) * 1000d);
            return root;
        }

        /// <summary>
        /// Build a long, tapered, arched and elliptical ram-air canopy (NOT square).
        /// Only the visual transforms bank; the player's collider stays vertical.
        /// The shape is seven curved double-sided colour panels with rope/harness rig.
        /// </summary>
        internal static GameObject CreateElongatedCanopy(Transform player)
        {
            var root = new GameObject("Aaris_ElongatedCanopy");
            root.transform.SetParent(player, false);
            const float harnessHeight = 1.55f;
            root.transform.localPosition = Vector3.up * harnessHeight;
            var rig = new GameObject("Canopy and suspension").transform;
            rig.SetParent(root.transform, false);
            rig.localPosition = Vector3.down * harnessHeight;

#if UNITY_ANDROID && !UNITY_EDITOR
            // Usually already loaded during aircraft flight. If the user opens
            // immediately before prewarm finishes, retain the exact original
            // model/fallback contract rather than showing a different canopy.
            var imported = prewarmedCanopyPrefab != null
                ? prewarmedCanopyPrefab
                : Resources.Load<GameObject>("AarisDrop/ElongatedParachute");
#else
            var imported = Resources.Load<GameObject>("AarisDrop/ElongatedParachute");
#endif
            if (imported != null)
            {
                var model = UnityEngine.Object.Instantiate(imported, rig, false);
                model.name = "LocalCanopyArt";
                // The OBJ is centered on the wing. Lift it to the same height as the procedural rig.
                model.transform.localPosition = Vector3.up * 4.25f;
                model.transform.localScale = Vector3.one;
                Material navy = Paint(ref canopyBlue, new Color(0.05f, 0.23f, 0.39f));
                Material ivory = Paint(ref canopyWhite, new Color(0.91f, 0.91f, 0.83f));
                Material orange = Paint(ref canopyOrange, new Color(0.97f, 0.45f, 0.13f));
                foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
                {
                    var slots = renderer.sharedMaterials;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        string label = slots[i] != null ? slots[i].name : "";
                        slots[i] = label.IndexOf("Orange", StringComparison.OrdinalIgnoreCase) >= 0
                            ? orange : label.IndexOf("White", StringComparison.OrdinalIgnoreCase) >= 0
                            ? ivory : navy;
                    }
                    renderer.sharedMaterials = slots;
                }

                // An imported OBJ's pivot is not guaranteed to coincide with
                // its visible wing. Align its actual renderer bounds with the
                // known suspension rig; otherwise some Android imports put
                // the canopy underneath the player, making him appear to
                // stand ON the parachute. This is one bounds pass per drop,
                // never a per-frame mesh modification or scene change.
                var wings = model.GetComponentsInChildren<Renderer>(true);
                Bounds wingBounds = new Bounds();
                bool foundWing = false;
                foreach (var wing in wings)
                {
                    if (wing == null) continue;
                    if (!foundWing) { wingBounds = wing.bounds; foundWing = true; }
                    else wingBounds.Encapsulate(wing.bounds);
                }
                if (foundWing && wingBounds.size.sqrMagnitude > 0.01f)
                {
                    Vector3 target = rig.TransformPoint(new Vector3(0f, 4.35f, 0f));
                    model.transform.position += target - wingBounds.center;
                }
                // Keep locally generated suspension lines attached to the harness.
            }
            else
            {
                Material[] paints = {
                    Paint(ref canopyBlue, new Color(0.05f, 0.23f, 0.39f)),
                    Paint(ref canopyWhite, new Color(0.91f, 0.91f, 0.83f)),
                    Paint(ref canopyOrange, new Color(0.97f, 0.45f, 0.13f))
                };
                for (int panel = 0; panel < 7; panel++)
                {
                    var segment = new GameObject("CurvedCanopyPanel_" + panel);
                    segment.transform.SetParent(rig, false);
                    var filter = segment.AddComponent<MeshFilter>();
                    filter.sharedMesh = PanelMesh(panel, 7);
                    var renderer = segment.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = paints[panel == 3 ? 2 : panel % 2];
                }
            }

            Material cords = Paint(ref rope, new Color(0.09f, 0.12f, 0.15f));
            for (int i = 0; i < 9; i++)
            {
                float s = Mathf.Lerp(-0.91f, 0.91f, i / 8f);
                Vector3 edge = CanopyPoint(s, 0.13f);
                Vector3 harness = new Vector3(s < 0f ? -0.23f : 0.23f, 1.55f, 0.12f);
                RigidLine(rig, "LeftSuspension_" + i, edge, harness, 0.011f, cords);
                if (i % 2 == 0)
                    RigidLine(rig, "RearSuspension_" + i,
                        CanopyPoint(s, 0.88f), harness + Vector3.back * 0.2f, 0.009f, cords);
            }
            return root;
        }

        private static Vector3 CanopyPoint(float lateral, float chord)
        {
            // Taper smoothly at the wing tips, giving the long elliptical Free Fire-style outline.
            float s = Mathf.Clamp(lateral, -1f, 1f);
            float spread = Mathf.Sqrt(Mathf.Max(0.01f, 1f - s * s));
            float x = s * 4.55f;
            float y = 4.55f - 1.23f * Mathf.Pow(Mathf.Abs(s), 1.62f)
                + 0.31f * Mathf.Sin(chord * Mathf.PI);
            float z = (chord - 0.5f) * 2.65f * spread;
            return new Vector3(x, y, z);
        }

        private static Mesh PanelMesh(int panel, int count)
        {
            const int along = 6, across = 8;
            var points = new Vector3[(along + 1) * (across + 1) * 2];
            var uv = new Vector2[points.Length];
            int rowSize = (along + 1) * (across + 1);
            for (int side = 0; side < 2; side++)
            for (int i = 0; i <= along; i++)
            for (int j = 0; j <= across; j++)
            {
                float s = Mathf.Lerp(-1f, 1f, (panel + i / (float)along) / count);
                float chord = j / (float)across;
                int idx = side * rowSize + i * (across + 1) + j;
                points[idx] = CanopyPoint(s, chord) + Vector3.up * (side == 0 ? 0.012f : -0.012f);
                uv[idx] = new Vector2((s + 1f) * 0.5f, chord);
            }
            var triangles = new int[along * across * 12];
            int t = 0;
            for (int side = 0; side < 2; side++)
            for (int i = 0; i < along; i++)
            for (int j = 0; j < across; j++)
            {
                int a = side * rowSize + i * (across + 1) + j;
                int b = a + across + 1;
                int c = a + 1;
                int d = b + 1;
                if (side == 0)
                {
                    triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
                    triangles[t++] = c; triangles[t++] = b; triangles[t++] = d;
                }
                else
                {
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = c; triangles[t++] = d; triangles[t++] = b;
                }
            }
            var mesh = new Mesh { name = "AarisOriginal_ElongatedPanel_" + panel };
            mesh.vertices = points;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
