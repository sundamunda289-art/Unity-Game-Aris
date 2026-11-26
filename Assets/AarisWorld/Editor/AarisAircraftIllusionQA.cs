#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Real imported aircraft, production flight/culling, native shader bytecode,
    /// and Camera.Render evidence. No saved scene, mesh or material is modified.
    /// Tight-loop managed allocations are not whole-frame/device performance.
    /// </summary>
    public static class AarisAircraftIllusionQA
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] MaterialNames = {
            "HulledMetal", "DarkCockpit", "WingPanels", "EngineDark", "SafetyAccent"
        };
        private static readonly string[] ShaderNames = { "AarisAircraftPropeller", "AarisWingtipCondensation" };
        private static readonly string[] PassNames = { "AircraftExposure", "WingtipWake" };
        private static StringBuilder report;
        private static int passed, failed;
        private static string OutputDirectory
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable("AARIS_AIRCRAFT_QA_DIR");
                return string.IsNullOrEmpty(configured)
                    ? Path.Combine(Path.GetTempPath(), "aaris-aircraft-illusion-qa") : configured;
            }
        }
        private static void Begin(string mode)
        {
            report = new StringBuilder("UNITY=" + Application.unityVersion + " MODE=" + mode + "\n");
            passed = failed = 0;
            Directory.CreateDirectory(OutputDirectory);
        }
        private static void Check(bool condition, string label)
        {
            if (condition) passed++; else failed++;
            report.AppendLine((condition ? "PASS " : "FAIL ") + label);
        }
        private static void Complete(string mode)
        {
            report.AppendLine("PASSED=" + passed + " FAILED=" + failed);
            report.AppendLine(failed == 0 ? "AARIS_AIRCRAFT_" + mode.ToUpperInvariant() + "_PASS" :
                "AARIS_AIRCRAFT_" + mode.ToUpperInvariant() + "_FAIL");
            File.WriteAllText(Path.Combine(OutputDirectory, mode + ".txt"), report.ToString());
            Debug.Log(report.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(failed == 0 ? 0 : 1);
        }
        private static GameObject CreateAircraft(Vector3 position, GameObject source = null,
            bool highDetail = false, bool preloaded = false)
        {
            Type type = typeof(AarisOfflineDrop).Assembly.GetType("Aaris.MapReview.AarisOfflineDropVisuals", true);
            object[] arguments = { position, Quaternion.identity, null, source, highDetail, preloaded };
            return (GameObject)type.GetMethod("CreatePlane", Static).Invoke(null, arguments);
        }
        private static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, Instance).SetValue(target, value);
        private static MethodInfo Method(string name) => typeof(AarisOfflineDrop).GetMethod(name, Instance);
        private static long TriangleCount(GameObject root)
        {
            long count = 0;
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null)
                    for (int i = 0; i < filter.sharedMesh.subMeshCount; i++)
                        count += (long)filter.sharedMesh.GetIndexCount(i) / 3;
            return count;
        }

        [MenuItem("Aaris/CI/Verify aircraft kinematics and GPU effects")]
        public static void Run()
        {
            Begin("runtime");
            GameObject plane = null, holder = null, synthetic = null, guarded = null;
            Camera camera = null;
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                    throw new InvalidOperationException("EDITOR_BUSY");
                var savedMaterials = new Material[MaterialNames.Length];
                var savedJson = new string[savedMaterials.Length];
                for (int i = 0; i < savedMaterials.Length; i++)
                {
                    savedMaterials[i] = Resources.Load<Material>("AarisDrop/Materials/CargoPlane-" + MaterialNames[i]);
                    if (savedMaterials[i] == null) throw new InvalidOperationException("AIRCRAFT_MATERIAL_MISSING");
                    savedJson[i] = EditorJsonUtility.ToJson(savedMaterials[i]);
                }
                GameObject prefab = Resources.Load<GameObject>("AarisDrop/CargoPlaneRealistic");
                if (prefab == null) throw new InvalidOperationException("AUTHORED_AIRCRAFT_MISSING");
                plane = CreateAircraft(Vector3.zero);
                Check(plane.GetComponentsInChildren<Collider>(true).Length == 0, "aircraft has no Collider components");
                Check(plane.GetComponentsInChildren<Rigidbody>(true).Length == 0, "aircraft has no Rigidbody components");
                var originalMeshes = new Dictionary<Mesh, int>();
                foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null) continue;
                    if (!originalMeshes.ContainsKey(filter.sharedMesh)) originalMeshes[filter.sharedMesh] = 0;
                    originalMeshes[filter.sharedMesh]++;
                }
                foreach (MeshFilter filter in plane.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null && originalMeshes.ContainsKey(filter.sharedMesh))
                        originalMeshes[filter.sharedMesh]--;
                bool identicalBody = true;
                foreach (int remaining in originalMeshes.Values) identicalBody &= remaining == 0;
                Check(identicalBody, "every authored body mesh is retained once");
                long nativeBodyTriangles = TriangleCount(prefab);
                long instanceBodyTriangles = 0;
                foreach (MeshFilter filter in plane.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null && originalMeshes.ContainsKey(filter.sharedMesh))
                        for (int submesh = 0; submesh < filter.sharedMesh.subMeshCount; submesh++)
                            instanceBodyTriangles += (long)filter.sharedMesh.GetIndexCount(submesh) / 3;
                Check(nativeBodyTriangles > 0 && instanceBodyTriangles == nativeBodyTriangles,
                    "native source/instance body triangle budget identical: " + nativeBodyTriangles);
                long objFaces = 0, objTriangles = 0;
                foreach (string line in File.ReadLines("Assets/Resources/AarisDrop/CargoPlaneRealistic.obj"))
                {
                    if (!line.StartsWith("f ", StringComparison.Ordinal)) continue;
                    int vertices = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length - 1;
                    objFaces++;
                    objTriangles += Math.Max(0, vertices - 2);
                }
                report.AppendLine("SOURCE_OBJ_FACES=" + objFaces + " SOURCE_OBJ_TRIANGLES=" + objTriangles +
                    " NATIVE_BODY_TRIANGLES=" + nativeBodyTriangles + " INSTANCE_BODY_TRIANGLES=" + instanceBodyTriangles);
                // Unity's imported submesh/hierarchy topology is authoritative
                // for rendering. Raw OBJ faces need not equal native index data.
                Check(objFaces == 42238 && objTriangles == 42238, "authored OBJ triangular topology remains intact");
                Transform rotors = plane.transform.Find("GpuPropellerDiscs");
                Transform wake = plane.transform.Find("WingtipCondensation");
                Check(rotors != null && wake != null, "combined rotor and wake renderers exist");
                if (rotors == null || wake == null) throw new InvalidOperationException("EFFECTS_MISSING");
                Mesh rotorMesh = rotors.GetComponent<MeshFilter>().sharedMesh;
                Mesh wakeMesh = wake.GetComponent<MeshFilter>().sharedMesh;
                Check(rotorMesh.triangles.Length / 3 == 48, "twin propellers total 48 triangles (baseline 264)");
                Check(wakeMesh.triangles.Length / 3 == 16, "both wingtip wakes total 16 triangles");
                Check(rotors.GetComponentsInChildren<Renderer>().Length == 1 &&
                    wake.GetComponentsInChildren<Renderer>().Length == 1,
                    "effects require two renderers combined (baseline rotors alone four)");
                Check(rotorMesh.bounds.center.z > 3.7f, "rotor disc remains in front of nacelles");
                Check(wakeMesh.bounds.size.x > 24f && wakeMesh.bounds.max.z < .37f,
                    "condensation attaches to actual outer trailing tips");
                Check(plane.GetComponentsInChildren<ParticleSystem>(true).Length == 0 &&
                    plane.GetComponentsInChildren<TrailRenderer>(true).Length == 0,
                    "no particles or trail simulation");
                Check(plane.GetComponentsInChildren<Light>(true).Length == 0, "aircraft creates no real Light components");
                foreach (Transform effect in new[] { rotors, wake })
                {
                    var renderer = effect.GetComponent<MeshRenderer>();
                    Check(renderer.shadowCastingMode == ShadowCastingMode.Off && !renderer.receiveShadows,
                        effect.name + " adds no shadow pass");
                    Check(renderer.sharedMaterial.shader != null && renderer.sharedMaterial.shader.isSupported,
                        effect.name + " native shader supported");
                }
                for (int i = 0; i < savedMaterials.Length; i++)
                {
                    Check(EditorJsonUtility.ToJson(savedMaterials[i]) == savedJson[i],
                        "saved " + MaterialNames[i] + " material unmodified");
                    bool match = false;
                    foreach (Renderer renderer in plane.GetComponentsInChildren<Renderer>(true))
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null || !material.name.StartsWith(savedMaterials[i].name, StringComparison.Ordinal)) continue;
                        match = material.shader == savedMaterials[i].shader &&
                            Mathf.Approximately(material.GetFloat("_Metallic"), savedMaterials[i].GetFloat("_Metallic")) &&
                            Mathf.Approximately(material.GetFloat("_Smoothness"), savedMaterials[i].GetFloat("_Smoothness")) &&
                            material.GetTexture("_BumpMap") == savedMaterials[i].GetTexture("_BumpMap");
                    }
                    Check(match, MaterialNames[i] + " keeps PBR shader, finish and normal-map reference");
                }
                camera = new GameObject("Aircraft QA observer").AddComponent<Camera>();
                camera.enabled = false;
                camera.farClipPlane = 5000f;
                camera.fieldOfView = 60f;
                camera.aspect = 16f / 9f;
                camera.transform.position = new Vector3(0f, 0f, -30f);
                holder = new GameObject("Aircraft QA inactive drop host");
                holder.SetActive(false);
                var drop = holder.AddComponent<AarisOfflineDrop>();
                Set(drop, "plane", plane);
                Set(drop, "view", camera);
                Set(drop, "planeFlightDirection", Vector3.forward);
                Set(drop, "planeHeadingRotation", Quaternion.identity);
                Set(drop, "planeSpeed", 76f);
                var advance = (Action<float>)Delegate.CreateDelegate(typeof(Action<float>), drop, Method("AdvanceDepartingAircraft"));
                var shouldRetire = (Func<float, bool>)Delegate.CreateDelegate(typeof(Func<float, bool>), drop, Method("ShouldRetireAircraft"));
                Quaternion rotorRotation = rotors.localRotation;
                Vector3 rotorPosition = rotors.localPosition;
                advance(.05f);
                Check(Mathf.Abs(plane.transform.position.z - 3.8f) < .0001f, "kinematic departure advances 76 m/s");
                Check(rotors.localRotation == rotorRotation && rotors.localPosition == rotorPosition,
                    "flight performs no local rotor transform animation");
                plane.transform.position = new Vector3(0f, 0f, 1100f);
                Check(!shouldRetire(10f) && !shouldRetire(20f), "visible distant aircraft survives beyond old 4.5-second timeout");
                camera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                Check(!shouldRetire(21f), "offscreen aircraft begins retirement grace");
                Check(!shouldRetire(22f), "brief glance away does not retire airplane");
                Check(shouldRetire(23.1f), "distant offscreen aircraft retires after two-second grace");
                camera.transform.rotation = Quaternion.identity;
                Check(!shouldRetire(24f), "looking back resets retirement grace");
                plane.transform.position = new Vector3(0f, 0f, -400f);
                Check(!shouldRetire(25f), "near offscreen aircraft retained");
                plane.transform.position = new Vector3(0f, 0f, 1100f);
                for (int i = 0; i < 128; i++) shouldRetire(30f + i);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 2048; i++) shouldRetire(200f + i);
                long visibilityBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(visibilityBytes == 0, "2048 warmed production frustum checks allocate " + visibilityBytes + " managed bytes");
                for (int i = 0; i < 128; i++) advance(.001f);
                before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 2048; i++) advance(.001f);
                long flightBytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(flightBytes == 0, "2048 warmed production flight steps allocate " + flightBytes + " managed bytes");
                Check(rotors.localRotation == rotorRotation && rotors.localPosition == rotorPosition,
                    "rotor local pose remains unchanged through 2176 flight steps");
                Method("RetireAircraft").Invoke(drop, null);
                Check(plane == null, "retirement releases actual aircraft hierarchy");
                plane = null;
                // A future prefab must not accidentally introduce simulated flight.
                synthetic = new GameObject("QA future prefab with accidental physics");
                synthetic.SetActive(false);
                synthetic.AddComponent<BoxCollider>();
                synthetic.AddComponent<Rigidbody>();
                guarded = CreateAircraft(Vector3.zero, synthetic, false, true);
                Check(guarded.GetComponentsInChildren<Collider>(true).Length == 0 &&
                    guarded.GetComponentsInChildren<Rigidbody>(true).Length == 0,
                    "future prefab physics is removed before visual flight");
                Check(guarded.transform.Find("GpuPropellerDiscs").GetComponent<MeshFilter>().sharedMesh.triangles.Length / 3 == 96,
                    "four-engine fallback uses one bounded 96-triangle rotor mesh");
            }
            catch (Exception exception) { Check(false, exception.ToString()); }
            finally
            {
                if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
                if (plane != null) UnityEngine.Object.DestroyImmediate(plane);
                if (guarded != null) UnityEngine.Object.DestroyImmediate(guarded);
                if (synthetic != null) UnityEngine.Object.DestroyImmediate(synthetic);
                if (camera != null) UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }
            Complete("runtime");
        }

        [MenuItem("Aaris/CI/Compile aircraft effects for desktop and Android")]
        public static void RunShaderVariants()
        {
            Begin("shaders");
            try
            {
                var platforms = new[] { ShaderCompilerPlatform.OpenGLCore, ShaderCompilerPlatform.GLES3x, ShaderCompilerPlatform.Vulkan };
                for (int i = 0; i < ShaderNames.Length; i++)
                {
                    Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Resources/" + ShaderNames[i] + ".shader");
                    if (shader == null) throw new InvalidOperationException("SHADER_MISSING " + ShaderNames[i]);
                    ShaderData data = ShaderUtil.GetShaderData(shader);
                    ShaderData.Pass pass = data.GetSubshader(0).GetPass(0);
                    Check(pass.Name == PassNames[i], "expected native effect pass " + pass.Name);
                    foreach (ShaderCompilerPlatform platform in platforms)
                    foreach (bool fogAndInstancing in new[] { false, true })
                    {
                        BuildTarget target = platform == ShaderCompilerPlatform.OpenGLCore
                            ? BuildTarget.StandaloneLinux64 : BuildTarget.Android;
                        string label = ShaderNames[i] + "/" + platform + (fogAndInstancing ? "/FOG_LINEAR+INSTANCING" : "/base");
                        ShaderData.VariantCompileInfo variant = pass.CompileVariant(ShaderType.Vertex,
                            fogAndInstancing ? new[] { "FOG_LINEAR", "INSTANCING_ON" } : Array.Empty<string>(), platform, target);
                        bool errors = false;
                        if (variant.Messages != null)
                            foreach (ShaderMessage message in variant.Messages)
                            {
                                bool error = message.severity == ShaderCompilerMessageSeverity.Error;
                                errors |= error;
                                report.AppendLine((error ? "ERROR " : "WARNING ") + label + " " + message.message);
                            }
                        Check(variant.Success && !errors && variant.ShaderData != null && variant.ShaderData.Length > 0,
                            label + " native bytecode");
                    }
                }
                report.AppendLine("Android shader bytecode is not an APK/device performance test.");
            }
            catch (Exception exception) { Check(false, exception.ToString()); }
            Complete("shaders");
        }

        [MenuItem("Aaris/CI/Capture actual aircraft shader and near rear views")]
        public static void Capture()
        {
            Begin("render");
            GameObject plane = null;
            Camera camera = null;
            Material rotorCopy = null;
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    throw new InvalidOperationException("NATIVE_GRAPHICS_REQUIRED");
                EditorSceneManager.OpenScene("Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity", OpenSceneMode.Single);
                Vector3 position = new Vector3(0f, 560f, 0f);
                plane = CreateAircraft(position);
                camera = new GameObject("Aircraft native capture camera").AddComponent<Camera>();
                camera.enabled = false;
                camera.allowHDR = false;
                camera.nearClipPlane = .05f;
                camera.farClipPlane = 6000f;
                camera.fieldOfView = 50f;
                camera.aspect = 16f / 9f;
                camera.clearFlags = CameraClearFlags.Skybox;
                CaptureView(camera, position + new Vector3(27f, 13f, 36f), position,
                    "aircraft-front.png");
                CaptureView(camera, position + new Vector3(-23f, 14f, -36f), position,
                    "aircraft-rear.png");
                CaptureView(camera, position + new Vector3(60f, -15f, -200f), position,
                    "aircraft-departing.png");
                // A short orthographic view at aircraft altitude excludes the
                // terrain and animated sky while retaining actual URP sunlight.
                // Keep the production layer: the active RendererData may reject
                // arbitrary test-only layers even when Camera.cullingMask admits them.
                var rotor = plane.transform.Find("GpuPropellerDiscs");
                var renderer = rotor.GetComponent<MeshRenderer>();
                Mesh rotorMesh = rotor.GetComponent<MeshFilter>().sharedMesh;
                Vector3[] vertices = rotorMesh.vertices;
                Vector3 rotorCenter = rotor.TransformPoint(vertices[0]);
                Vector3 rotorNormal = rotor.TransformDirection(rotorMesh.normals[0]).normalized;
                float rotorRadius = Vector3.Distance(rotorCenter, rotor.TransformPoint(vertices[1]));
                camera.cullingMask = ~0;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.32f, .40f, .48f);
                camera.orthographic = true;
                camera.orthographicSize = rotorRadius * 1.16f;
                camera.farClipPlane = Mathf.Max(40f, rotorRadius * 20f);
                camera.transform.SetPositionAndRotation(rotorCenter + rotorNormal * (rotorRadius * 4f),
                    Quaternion.LookRotation(-rotorNormal, rotor.up));
                Vector3 viewportCenter = camera.WorldToViewportPoint(rotorCenter);
                Check(rotorRadius > .1f && viewportCenter.z > camera.nearClipPlane &&
                    Mathf.Abs(viewportCenter.x - .5f) < .001f && Mathf.Abs(viewportCenter.y - .5f) < .001f &&
                    GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(camera), renderer.bounds),
                    "actual transformed rotor occupies the isolated camera frustum");
                report.AppendLine("ROTOR_CENTER=" + rotorCenter + " RADIUS=" + rotorRadius +
                    " WORLD_BOUNDS=" + renderer.bounds + " LAYER=" + rotor.gameObject.layer);
                rotorCopy = new Material(renderer.sharedMaterial);
                renderer.sharedMaterial = rotorCopy;
                Quaternion beforeRotation = rotor.localRotation;
                Vector3 beforePosition = rotor.localPosition;
                rotorCopy.SetFloat("_TimeOffset", 0f);
                Color32[] first = WritePng(camera, "rotor-gpu-time-a.png");
                rotorCopy.SetFloat("_TimeOffset", .012f);
                Color32[] second = WritePng(camera, "rotor-gpu-time-b.png");
                int changed = ChangedPixels(first, second);
                Check(changed > 100, "GPU exposure changes " + changed + " pixels with no CPU rotor movement");
                Check(rotor.localRotation == beforeRotation && rotor.localPosition == beforePosition,
                    "native GPU animation retains static rotor local transform");
                rotorCopy.SetFloat("_RPM", 0f);
                Color32[] stopped = WritePng(camera, "rotor-stopped-blades.png");
                Check(ChangedPixels(second, stopped) > 100,
                    "native stopped blades differ from the running exposure disc");
                report.AppendLine("Render source: actual imported CC0 aircraft with existing scene lighting; no scene/material saved.");
            }
            catch (Exception exception) { Check(false, exception.ToString()); }
            finally
            {
                if (plane != null) UnityEngine.Object.DestroyImmediate(plane);
                if (camera != null) UnityEngine.Object.DestroyImmediate(camera.gameObject);
                if (rotorCopy != null) UnityEngine.Object.DestroyImmediate(rotorCopy);
            }
            Complete("render");
        }
        private static void CaptureView(Camera camera, Vector3 eye, Vector3 focus, string name)
        {
            camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(focus - eye, Vector3.up));
            WritePng(camera, name);
        }
        private static int ChangedPixels(Color32[] first, Color32[] second)
        {
            int changed = 0;
            for (int i = 0; i < first.Length; i++)
                if (Math.Abs(first[i].r - second[i].r) + Math.Abs(first[i].g - second[i].g) +
                    Math.Abs(first[i].b - second[i].b) >= 3) changed++;
            return changed;
        }
        private static Color32[] WritePng(Camera camera, string name)
        {
            const int width = 1280, height = 720;
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                image.Apply(false);
                Color32[] pixels = image.GetPixels32();
                int magenta = 0, spatiallyDifferent = 0;
                int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
                Color32 corner = pixels[0];
                foreach (Color32 pixel in pixels)
                {
                    if (pixel.r > 220 && pixel.b > 220 && pixel.g < 60) magenta++;
                    minR = Math.Min(minR, pixel.r); maxR = Math.Max(maxR, pixel.r);
                    minG = Math.Min(minG, pixel.g); maxG = Math.Max(maxG, pixel.g);
                    minB = Math.Min(minB, pixel.b); maxB = Math.Max(maxB, pixel.b);
                    if (Math.Abs(pixel.r - corner.r) + Math.Abs(pixel.g - corner.g) +
                        Math.Abs(pixel.b - corner.b) >= 3) spatiallyDifferent++;
                }
                int spatialRange = Math.Max(maxR - minR, Math.Max(maxG - minG, maxB - minB));
                // Different R/G/B channels in a solid blue clear buffer are
                // not spatial image content. Require actual varying pixels.
                Check(magenta < pixels.Length / 1000 && spatialRange > 20 && spatiallyDifferent > 100,
                    name + " nonblank native pixels, spatial=" + spatiallyDifferent +
                    " range=" + spatialRange + " magenta=" + magenta);
                File.WriteAllBytes(Path.Combine(OutputDirectory, name), image.EncodeToPNG());
                return pixels;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(image);
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
#endif
