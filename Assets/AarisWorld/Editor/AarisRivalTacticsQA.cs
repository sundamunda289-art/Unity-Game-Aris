#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>Executes production movement/spacing methods and real PhysX LOS.
    /// Cached delegates keep the measured region free of reflection allocations.
    /// It does not claim device frame time or full-frame zero allocation.</summary>
    public static class AarisRivalTacticsQA
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type Bot = typeof(AarisOfflineBRDirector).Assembly.GetType("Aaris.MapReview.AarisOfflineRival", true);
        private static readonly Vector3 Origin = new Vector3(15000f, 5000f, 15000f);
        private static readonly StringBuilder Report = new StringBuilder();
        private static int checks;

        private static void Check(bool condition, string label)
        {
            Report.AppendLine((condition ? "PASS " : "FAIL ") + label);
            if (!condition) throw new InvalidOperationException(label);
            checks++;
        }
        private static FieldInfo Field(Type type, string name) => type.GetField(name, Instance | Static) ??
            throw new MissingFieldException(type.Name, name);
        private static void Set(object subject, string name, object value) => Field(subject.GetType(), name).SetValue(subject, value);
        private static T Bound<T>(object subject, string name) where T : Delegate =>
            (T)Delegate.CreateDelegate(typeof(T), subject, subject.GetType().GetMethod(name, Instance));
        private static T Unbound<T>(string name) where T : Delegate =>
            (T)Delegate.CreateDelegate(typeof(T), Bot.GetMethod(name, Static));

        private static string OutputDirectory
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable("AARIS_GAMEPLAY_QA_OUT");
                return string.IsNullOrEmpty(configured)
                    ? Path.Combine(Path.GetTempPath(), "aaris-rival-tactics-qa") : configured;
            }
        }

        private static void Finish(string name, int exit)
        {
            Directory.CreateDirectory(OutputDirectory);
            File.WriteAllText(Path.Combine(OutputDirectory, name), Report.ToString());
            Debug.Log(Report.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(exit);
        }

        public static void Run()
        {
            Report.Clear(); checks = 0;
            Report.AppendLine("Unity " + Application.unityVersion);
            int exit = 0;
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                    throw new InvalidOperationException("EDITOR_BUSY");
                VerifyLocomotion();
                VerifyTacticalRanges();
                VerifySpacingAndGc();
                VerifyCoverReservations();
                VerifyNativeRangedSight();
                VerifyPresentationResources();
                Report.AppendLine("RESULT=PASS checks=" + checks);
            }
            catch (Exception ex)
            {
                exit = 1;
                Report.AppendLine("RESULT=FAIL " + ex);
            }
            Finish("rival-tactics.txt", exit);
        }

        public static void RunShaderVariants()
        {
            Report.Clear(); checks = 0;
            Report.AppendLine("Unity " + Application.unityVersion + " native rival shader variants");
            int exit = 0;
            try
            {
                var platforms = new[] { ShaderCompilerPlatform.OpenGLCore,
                    ShaderCompilerPlatform.GLES3x, ShaderCompilerPlatform.Vulkan };
                var variants = new[] { Array.Empty<string>(), new[] { "FOG_LINEAR" },
                    new[] { "INSTANCING_ON" }, new[] { "FOG_LINEAR", "INSTANCING_ON" } };
                var depthVariants = new[] { Array.Empty<string>(), new[] { "INSTANCING_ON" } };
                var normalVariants = new[] { Array.Empty<string>(), new[] { "INSTANCING_ON" },
                    new[] { "_GBUFFER_NORMALS_OCT" }, new[] { "_GBUFFER_NORMALS_OCT", "INSTANCING_ON" } };
                foreach (string name in new[] { "AarisRivalContact", "AarisRivalReadability" })
                {
                    Shader shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Resources/" + name + ".shader");
                    if (shader == null) throw new InvalidOperationException("SHADER_MISSING " + name);
                    ShaderData.Subshader subshader = ShaderUtil.GetShaderData(shader).GetSubshader(0);
                    for (int passIndex = 0; passIndex < subshader.PassCount; passIndex++)
                    {
                        ShaderData.Pass pass = subshader.GetPass(passIndex);
                        string[][] passVariants = pass.Name == "DepthOnly" ? depthVariants :
                            pass.Name == "DepthNormalsOnly" ? normalVariants : variants;
                        foreach (ShaderCompilerPlatform platform in platforms)
                            foreach (string[] keywords in passVariants)
                            {
                                BuildTarget target = platform == ShaderCompilerPlatform.OpenGLCore
                                    ? BuildTarget.StandaloneLinux64 : BuildTarget.Android;
                                string label = name + "/" + pass.Name + "/" + platform + "/Vertex+Fragment/" +
                                    (keywords.Length == 0 ? "base" : string.Join("+", keywords));
                                // These backends return the combined vertex/fragment
                                // program through Vertex, as the existing native
                                // SpeedTree gate verified on this same Editor version.
                                ShaderData.VariantCompileInfo variant = pass.CompileVariant(ShaderType.Vertex, keywords, platform, target);
                                bool errors = false;
                                if (variant.Messages != null)
                                    foreach (ShaderMessage message in variant.Messages)
                                    {
                                        bool error = message.severity == ShaderCompilerMessageSeverity.Error;
                                        errors |= error;
                                        Report.AppendLine((error ? "ERROR " : "WARNING ") + label + " " + message.message);
                                    }
                                Check(variant.Success && !errors && variant.ShaderData != null && variant.ShaderData.Length > 0,
                                    label + " native bytecode");
                            }
                    }
                }
                Report.AppendLine("RESULT=PASS checks=" + checks);
                Report.AppendLine("Android bytecode is not a Snapdragon device frame-time measurement.");
            }
            catch (Exception ex) { exit = 1; Report.AppendLine("RESULT=FAIL " + ex); }
            Finish("rival-shaders.txt", exit);
        }

        public static void Capture()
        {
            Report.Clear(); checks = 0;
            Report.AppendLine("Unity " + Application.unityVersion + " actual isolated Camera.Render evidence");
            int exit = 0;
            GameObject root = null;
            AarisOfflineBRDirector captureDirector = null;
            Material groundMaterial = null;
            object presentation = null;
            PlayableGraph graph = default;
            var active = Field(typeof(AarisOfflineBRDirector), "activeDirector");
            object previousDirector = active.GetValue(null);
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    throw new InvalidOperationException("NATIVE_GRAPHICS_REQUIRED");
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(.45f, .49f, .53f);
                RenderSettings.fog = false;
                root = new GameObject("Temporary rival presentation capture");
                var director = root.AddComponent<AarisOfflineBRDirector>(); director.enabled = false;
                captureDirector = director;
                var sun = new GameObject("Controlled daylight").AddComponent<Light>();
                sun.transform.SetParent(root.transform); sun.type = LightType.Directional;
                sun.intensity = 1.5f; sun.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
                sun.shadows = LightShadows.None; // Isolate the optional contact's actual pixels.
                RenderSettings.sun = sun;
                var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ground.name = "Native contact receiver"; ground.transform.SetParent(root.transform);
                ground.transform.localPosition = new Vector3(0f, -.1f, 0f);
                ground.transform.localScale = new Vector3(30f, .2f, 30f);
                groundMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                groundMaterial.SetColor("_BaseColor", new Color(.58f, .62f, .55f));
                ground.GetComponent<MeshRenderer>().sharedMaterial = groundMaterial;
                var prefab = Resources.Load<GameObject>("AarisCharacters/Primary/RealisticSoldier/RealisticSoldier");
                Check(prefab != null, "Bundled real soldier asset loaded");
                GameObject soldier = Object.Instantiate(prefab, root.transform);
                soldier.name = "Actual bundled near soldier";
                foreach (Collider collider in soldier.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (Rigidbody body in soldier.GetComponentsInChildren<Rigidbody>(true))
                { body.isKinematic = true; body.detectCollisions = false; }
                var conversion = Bound<Func<Material, Material>>(director, "CompatibleRivalMaterial");
                foreach (Renderer renderer in soldier.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] materials = renderer.sharedMaterials;
                    for (int i = 0; i < materials.Length; i++) materials[i] = conversion(materials[i]);
                    renderer.sharedMaterials = materials;
                }
                Animator animator = soldier.GetComponentInChildren<Animator>(true);
                if (animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman)
                {
                    AnimationClip idle = null;
                    foreach (AnimationClip clip in Resources.LoadAll<AnimationClip>("AarisCharacters/Primary/Animations/idle"))
                        if (clip != null && !clip.name.StartsWith("__preview__", StringComparison.Ordinal)) { idle = clip; break; }
                    if (idle != null)
                    {
                        animator.applyRootMotion = false; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                        graph = PlayableGraph.Create("Isolated real soldier idle capture");
                        AnimationPlayableOutput.Create(graph, "Idle", animator).SetSourcePlayable(AnimationClipPlayable.Create(graph, idle));
                        graph.Play(); graph.Evaluate(.1f);
                    }
                }
                Bounds bounds = new Bounds(); bool found = false;
                foreach (Renderer renderer in soldier.GetComponentsInChildren<Renderer>(true))
                { if (!found) { bounds = renderer.bounds; found = true; } else bounds.Encapsulate(renderer.bounds); }
                Check(found && bounds.size.y > .01f, "Actual soldier has native renderable bounds");
                soldier.transform.localScale *= 1.74f / bounds.size.y;
                float low = float.PositiveInfinity;
                foreach (Renderer renderer in soldier.GetComponentsInChildren<Renderer>(true)) low = Mathf.Min(low, renderer.bounds.min.y);
                soldier.transform.position += Vector3.up * (.025f - low);
                var baselineCamera = CaptureCamera(root.transform, "Baseline camera");
                var contactCamera = CaptureCamera(root.transform, "Contact camera");
                baselineCamera.transform.SetPositionAndRotation(new Vector3(2.7f, 1.9f, 4.2f),
                    Quaternion.LookRotation(new Vector3(0f,.85f,0f) - new Vector3(2.7f,1.9f,4.2f)));
                contactCamera.transform.SetPositionAndRotation(baselineCamera.transform.position, baselineCamera.transform.rotation);
                soldier.SetActive(false);
                Color32[] emptyNear = CapturePng(baselineCamera, "rival-near-ground-control.png", false);
                soldier.SetActive(true);
                if (graph.IsValid()) graph.Evaluate(0f);
                Color32[] before = CapturePng(baselineCamera, "rival-near-baseline.png");
                Check(ChangedPixels(emptyNear, before) > 100,
                    "Actual bundled near soldier changes native pixels against the same empty floor");
                Type presentationType = typeof(AarisOfflineBRDirector).Assembly.GetType("Aaris.MapReview.AarisRivalPresentation", true);
                presentation = Activator.CreateInstance(presentationType, Instance, null, new object[] { 100 }, null);
                var update = Bound<Action<int, Transform, Vector3, bool, float, float>>(presentation, "UpdateContact");
                var draw = Bound<Action<Camera, float>>(presentation, "Draw");
                Physics.SyncTransforms();
                update(0, soldier.transform, Vector3.up * .025f, true, 25f, 100f);
                draw(contactCamera, 100f);
                Check((int)presentationType.GetProperty("LastDrawCount", Instance).GetValue(presentation) == 1,
                    "One individually grounded visible contact enters one instanced batch");
                Color32[] after = CapturePng(contactCamera, "rival-near-soft-contact.png");
                int changed = 0;
                for (int i = 0; i < before.Length; i++)
                    if (before[i].r - after[i].r + before[i].g - after[i].g + before[i].b - after[i].b >= 5) changed++;
                Check(changed > 20, "Actual soft contact darkens " + changed + " native pixels");
                update(0, soldier.transform, Vector3.up * .6f, false, 25f, 100.01f);
                draw(contactCamera, 100.01f);
                Check((int)presentationType.GetProperty("LastDrawCount", Instance).GetValue(presentation) == 0,
                    "Airborne/ineligible contact is removed from the batch");
                VerifyContactProbeFairness(presentation, soldier.transform);
                soldier.SetActive(false);
                var distant = new GameObject("Original shared distant rival LOD", typeof(MeshFilter), typeof(MeshRenderer));
                distant.transform.SetParent(root.transform);
                Mesh distantMesh = Unbound<Func<Mesh>>("DistantRivalMesh")();
                distant.GetComponent<MeshFilter>().sharedMesh = distantMesh;
                MeshRenderer distantRenderer = distant.GetComponent<MeshRenderer>();
                Material distantMaterial = Unbound<Func<Material>>("DistantRivalMaterial")();
                distantRenderer.sharedMaterial = distantMaterial;
                baselineCamera.transform.SetPositionAndRotation(new Vector3(2.7f, 1.8f, 5f),
                    Quaternion.LookRotation(new Vector3(0f,.9f,0f) - new Vector3(2.7f,1.8f,5f)));
                distant.SetActive(false);
                Color32[] emptyFar = CapturePng(baselineCamera, "rival-distant-ground-control.png", false);
                distant.SetActive(true);
                bool inFrustum = GeometryUtility.TestPlanesAABB(
                    GeometryUtility.CalculateFrustumPlanes(baselineCamera), distantRenderer.bounds);
                Report.AppendLine("INFO Far renderer active=" + distant.activeInHierarchy +
                    " enabled=" + distantRenderer.enabled + " forceOff=" + distantRenderer.forceRenderingOff +
                    " layer=" + distant.layer + " cameraMask=" + baselineCamera.cullingMask +
                    " renderingLayerMask=" + distantRenderer.renderingLayerMask + " inFrustum=" + inFrustum +
                    " bounds=" + distantRenderer.bounds + " viewport=" +
                    baselineCamera.WorldToViewportPoint(distantRenderer.bounds.center) +
                    " vertices=" + distantMesh.vertexCount + " triangles=" + distantMesh.GetIndexCount(0) / 3);
                Report.AppendLine("INFO Far shader=" + distantMaterial.shader.name +
                    " supported=" + distantMaterial.shader.isSupported + " renderQueue=" + distantMaterial.renderQueue +
                    " passCount=" + distantMaterial.passCount + " pipeline=" + GraphicsSettings.currentRenderPipeline);
                for (int pass = 0; pass < distantMaterial.passCount; pass++)
                    Report.AppendLine("INFO Far pass[" + pass + "]=" + distantMaterial.GetPassName(pass));
                Color32[] farBody = CapturePng(baselineCamera, "rival-distant-original-mesh-new-shading.png");
                int farChanged = ChangedPixels(emptyFar, farBody);
                // The stock-material control isolates geometry/frustum from
                // custom-pass routing. It never substitutes for the production
                // shader's own required pixels below.
                var stockMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                try
                {
                    stockMaterial.SetColor("_BaseColor", distantMaterial.GetColor("_BaseColor"));
                    distantRenderer.sharedMaterial = stockMaterial;
                    Color32[] stockFar = CapturePng(baselineCamera, "rival-distant-stock-unlit-control.png");
                    int stockChanged = ChangedPixels(emptyFar, stockFar);
                    Report.AppendLine("INFO Identical far mesh productionChanged=" + farChanged +
                        " stockUnlitChanged=" + stockChanged);
                    Check(stockChanged > 100, "The identical production mesh renders under stock URP Unlit (" +
                        stockChanged + " changed pixels)");
                }
                finally
                {
                    distantRenderer.sharedMaterial = distantMaterial;
                    Object.DestroyImmediate(stockMaterial);
                }
                Check(farChanged > 100,
                    "Actual production far mesh changes " + farChanged + " native pixels against the same empty floor");
                RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = baselineCamera.backgroundColor;
                RenderSettings.fogStartDistance = 1f; RenderSettings.fogEndDistance = 12f;
                distant.SetActive(false);
                Color32[] fogControl = CapturePng(baselineCamera, "rival-distant-fog-control.png", false);
                distant.SetActive(true);
                Color32[] fogBody = CapturePng(baselineCamera, "rival-distant-world-fog.png");
                Check(ChangedPixels(fogControl, fogBody) > 100,
                    "Production far mesh remains genuinely rendered with world fog");
                Report.AppendLine("INFO Controlled isolated scene; real imported soldier and production shared LOD/contact APIs. No asset saved.");
                Report.AppendLine("RESULT=PASS checks=" + checks);
            }
            catch (Exception ex) { exit = 1; Report.AppendLine("RESULT=FAIL " + ex); }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                if (presentation is IDisposable disposable) disposable.Dispose();
                // Always clean owned conversion copies, including a failed capture.
                // Imported shared materials themselves are never destroyed.
                if (captureDirector != null)
                {
                    var cache = (System.Collections.IDictionary)Field(typeof(AarisOfflineBRDirector), "rivalMaterialCache").GetValue(captureDirector);
                    foreach (object material in cache.Values)
                        if (material is Material m) Object.DestroyImmediate(m);
                    cache.Clear();
                }
                if (groundMaterial != null) Object.DestroyImmediate(groundMaterial);
                if (root != null) Object.DestroyImmediate(root);
                active.SetValue(null, previousDirector);
            }
            Finish("rival-render.txt", exit);
        }

        private static void VerifyContactProbeFairness(object presentation, Transform actor)
        {
            var update = Bound<Action<int, Transform, Vector3, bool, float, float>>(presentation, "UpdateContact");
            var probe = Bound<Action<float, int>>(presentation, "ProbePendingContacts");
            bool bounded = true;
            // Invoke the actual native floor-query scheduler with an explicit
            // 30Hz frame clock. All100 submit in the same fixed order every
            // frame; this previously starved slots beyond the first16.
            for (int frame = 0; frame < 60; frame++)
            {
                float now = 200f + frame / 30f;
                for (int slot = 0; slot < 100; slot++)
                    update(slot, actor, new Vector3((slot % 10) * .1f, .025f,
                        (slot / 10) * .1f), true, 25f, now);
                probe(now, 100000 + frame);
                bounded &= (int)presentation.GetType().GetProperty("LastProbeCount", Instance).GetValue(presentation) <= 2;
            }
            bool[] sampled = (bool[])Field(presentation.GetType(), "grounded").GetValue(presentation);
            int count = 0; for (int i = 0; i < sampled.Length; i++) if (sampled[i]) count++;
            Check(bounded && count == 100,
                "Fair two-probe budget grounds all100 fixed-order contacts at30Hz (native floor rays)");
            for (int frame = 0; frame < 10; frame++)
            {
                float now = 203f + frame / 30f;
                for (int slot = 0; slot < 100; slot++)
                    update(slot, actor, new Vector3((slot % 10) * .1f, .025f,
                        (slot / 10) * .1f), slot != 0 && slot != 99, 25f, now);
                probe(now, 100060 + frame);
                bounded &= (int)presentation.GetType().GetProperty("LastProbeCount", Instance).GetValue(presentation) <= 2;
            }
            Check(bounded && !sampled[0] && !sampled[99],
                "Hidden/removed first and last slots cannot retain or block fair contact probes");
        }

        private static Camera CaptureCamera(Transform parent, string name)
        {
            var camera = new GameObject(name).AddComponent<Camera>();
            camera.transform.SetParent(parent);
            camera.enabled = true; // Production Draw deliberately rejects inactive cameras.
            camera.allowHDR = false; camera.nearClipPlane = .05f; camera.farClipPlane = 100f;
            camera.fieldOfView = 42f; camera.aspect = 1.5f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.49f,.59f,.68f);
            return camera;
        }

        private static int ChangedPixels(Color32[] first, Color32[] second)
        {
            int changed = 0;
            for (int i = 0; i < first.Length; i++)
                if (Math.Abs(first[i].r - second[i].r) + Math.Abs(first[i].g - second[i].g) +
                    Math.Abs(first[i].b - second[i].b) >= 8) changed++;
            return changed;
        }

        private static Color32[] CapturePng(Camera camera, string name, bool requireVariation = true)
        {
            const int width = 960, height = 640;
            RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            var image = new Texture2D(width,height,TextureFormat.RGB24,false);
            try
            {
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                image.ReadPixels(new Rect(0,0,width,height),0,0,false); image.Apply(false);
                Color32[] pixels = image.GetPixels32();
                int min=255,max=0,magenta=0;
                foreach(Color32 pixel in pixels)
                {
                    // Variation across PIXELS, not differences among RGB
                    // channels of one uniformly coloured clear buffer.
                    int luminance = (54 * pixel.r + 183 * pixel.g + 19 * pixel.b) >> 8;
                    min=Math.Min(min,luminance);
                    max=Math.Max(max,luminance);
                    if(pixel.r>220&&pixel.b>220&&pixel.g<60)magenta++;
                }
                Check((!requireVariation || max-min>20)&&magenta<pixels.Length/1000,
                    name+" spatial luminance range="+(max-min)+", magenta="+magenta);
                Directory.CreateDirectory(OutputDirectory);
                File.WriteAllBytes(Path.Combine(OutputDirectory,name),image.EncodeToPNG());
                return pixels;
            }
            finally
            {
                camera.targetTexture=null; RenderTexture.active=previous;
                Object.DestroyImmediate(image); RenderTexture.ReleaseTemporary(target);
            }
        }

        private static void VerifyLocomotion()
        {
            var step = Unbound<Func<Vector3, Vector3, float, float, float, Vector3>>("StepLocomotionVelocity");
            Vector3 v = step(Vector3.zero, Vector3.forward * 6.5f, .1f, 13f, 19f);
            Check(Mathf.Abs(v.z - 1.3f) < .00001f, "Acceleration is 13m/s2, not immediate sprint");
            float[] targetSpeeds = { 2f, 4.2f, 5.7f, 6.5f };
            int[] rates = { 15, 30, 60, 120 };
            for (int s = 0; s < targetSpeeds.Length; s++)
                for (int r = 0; r < rates.Length; r++)
                {
                    v = Vector3.zero;
                    float dt = 1f / rates[r];
                    bool bounded = true;
                    for (int frame = 0; frame < rates[r]; frame++)
                    {
                        Vector3 next = step(v, Vector3.forward * targetSpeeds[s], dt, 13f, 19f);
                        bounded &= next.z >= v.z && next.magnitude <= targetSpeeds[s] + .0001f &&
                            (next - v).magnitude <= 13f * dt + .0001f;
                        v = next;
                    }
                    Check(bounded && Mathf.Abs(v.z - targetSpeeds[s]) < .0001f,
                        "Monotonic player-matched speed " + targetSpeeds[s] + " at " + rates[r] + "Hz");
                    for (int frame = 0; frame < rates[r]; frame++)
                        v = step(v, Vector3.zero, dt, 13f, 19f);
                    Check(v.sqrMagnitude == 0f, "Braking settles exactly at zero " + targetSpeeds[s] + " / " + rates[r]);
                }
            v = step(Vector3.forward * 4.2f, Vector3.back * 4.2f, .1f, 13f, 19f);
            Check(Mathf.Abs(v.z - 2.3f) < .0001f, "Reverse direction uses player braking rather than snapping");
            Check(step(Vector3.forward, Vector3.back, 0f, 13f, 19f) == Vector3.forward,
                "Zero-time step leaves velocity unchanged");
            Check(step(Vector3.up, Vector3.forward, .1f, 13f, 19f).y == 0f,
                "Horizontal smoothing cannot inject vertical movement");
        }

        private static void VerifyTacticalRanges()
        {
            var range = Unbound<Func<AarisWeaponSpec, float>>("TacticalFireRange");
            string[] categories = { "Shotgun", "Pistol", "SMG", "AR", "DMR", "Sniper" };
            float[] expected = { 22f, 30f, 40f, 65f, 80f, 80f };
            for (int i = 0; i < categories.Length; i++)
                Check(range(new AarisWeaponSpec { category = categories[i], rangeMetres = 480f }) == expected[i],
                    "Weapon-aware range " + categories[i] + " = " + expected[i] + "m");
            Check(range(null) == 2.25f, "Unarmed reach remains contact-only");
            Check(range(new AarisWeaponSpec { category = "AR", rangeMetres = 15f }) == 15f,
                "Tactics never extend an authored short weapon range");
            var shot = Unbound<Func<Vector3, Vector3, Vector3, float, bool>>("IsPostMoveShotInRangeAndFacing");
            Check(shot(Vector3.zero, Vector3.forward, Vector3.forward * 65f, 65f) &&
                !shot(Vector3.zero, Vector3.forward, Vector3.forward * 65.001f, 65f),
                "Post-movement fire range retains exact inclusive boundary");
            Check(!shot(Vector3.zero, Vector3.back, Vector3.forward * 60f, 65f),
                "Long range never bypasses forward aiming gate");
            var settled = Unbound<Func<float,float,bool>>("IsRangedShotSettled");
            Check(!settled(4f, 1f) && !settled(0f, .119f) && settled(.45f * .45f, .12f),
                "Ranged fire requires actual speed<=0.45m/s for at least120ms");
            Check(!settled(.451f * .451f, 2f), "Ranged settle boundary rejects moving gunfire");
        }

        private static void VerifySpacingAndGc()
        {
            var root = new GameObject("Isolated rival tactical spacing QA");
            var active = Field(typeof(AarisOfflineBRDirector), "activeDirector");
            object previous = active.GetValue(null);
            try
            {
                var director = root.AddComponent<AarisOfflineBRDirector>();
                director.enabled = false;
                var roster = (Array)Field(typeof(AarisOfflineBRDirector), "rivals").GetValue(director);
                var bots = new Component[100];
                var positions = new Vector3[100];
                var velocities = new Vector3[100];
                var track = typeof(AarisOfflineBRDirector).GetMethod("TrackRivalPosition", Instance);
                var args = new object[3];
                for (int i = 0; i < 100; i++)
                {
                    var actor = new GameObject("Logical QA rival " + i);
                    actor.transform.SetParent(root.transform, false);
                    var hp = actor.AddComponent<AarisOfflineHealth>();
                    Set(hp, "current", 200f);
                    bots[i] = actor.AddComponent(Bot);
                    ((Behaviour)bots[i]).enabled = false;
                    Set(bots[i], "health", hp);
                    Set(bots[i], "spatialSlot", i);
                    roster.SetValue(bots[i], i);
                    positions[i] = Origin + new Vector3(i * 20f, 0f, 0f);
                    args[0] = i; args[1] = bots[i]; args[2] = positions[i];
                    track.Invoke(director, args);
                }
                var separate = Bound<Func<int, Vector3, Vector3>>(director, "ComputeRivalSeparation");
                var clear = Bound<Func<Vector3, int, float, bool>>(director, "IsRivalSpaceClear");
                var step = Unbound<Func<Vector3, Vector3, float, float, float, Vector3>>("StepLocomotionVelocity");
                Check(clear(Origin + Vector3.forward * 8f, -1, 8f) &&
                    !clear(Origin + Vector3.forward * 7.99f, -1, 8f),
                    "Spawn spacing rejects <8m and accepts exact8m");
                Check(clear(Origin, 0, 8f), "Spacing excludes the requesting actor");
                positions[1] = Origin;
                args[0] = 1; args[1] = bots[1]; args[2] = positions[1]; track.Invoke(director, args);
                Vector3 first = separate(0, Origin), second = separate(1, Origin);
                Check(first.sqrMagnitude > .9f && (first + second).sqrMagnitude < .00001f,
                    "Exactly coincident pair receives opposite nonzero separation");
                // Place across a negative-coordinate 8m grid seam.
                positions[0] = new Vector3(-8.01f, Origin.y, -.01f);
                positions[1] = new Vector3(-7.99f, Origin.y, .01f);
                for (int i = 0; i < 2; i++)
                { args[0] = i; args[1] = bots[i]; args[2] = positions[i]; track.Invoke(director, args); }
                Check(separate(0, positions[0]).sqrMagnitude > .8f &&
                    !clear(positions[0], 0, 4f), "Negative cell seams retain neighbours and occupied cover rejection");
                Set(Field(Bot, "health").GetValue(bots[1]), "dead", true);
                Check(separate(0, positions[0]).sqrMagnitude == 0f && clear(positions[0], 0, 4f),
                    "Dead rivals never repel or reserve cover");
                Set(Field(Bot, "health").GetValue(bots[1]), "dead", false);
                for (int i = 0; i < 100; i++)
                {
                    positions[i] = Origin + new Vector3((i % 10) * .8f, 0f, (i / 10) * .8f);
                    args[0] = i; args[1] = bots[i]; args[2] = positions[i]; track.Invoke(director, args);
                }
                float before = MeanNearestDistance(positions);
                for (int tick = 0; tick < 240; tick++)
                    for (int i = 0; i < 100; i++)
                    {
                        Vector3 desired = separate(i, positions[i]) * 2f;
                        velocities[i] = step(velocities[i], desired, .05f, 13f, 19f);
                        positions[i] += velocities[i] * .05f;
                        args[0] = i; args[1] = bots[i]; args[2] = positions[i]; track.Invoke(director, args);
                    }
                float after = MeanNearestDistance(positions);
                Report.AppendLine("MEASURE dense100_mean_nearest_before=" + before.ToString("F4") +
                    " after=" + after.ToString("F4"));
                Check(after > before * 2f, "100 crowded logical actors increase mean nearest spacing >2x");
                // All reflection, setup and formatting is outside the allocation
                // interval. These are exactly the production managed hot helpers.
                Vector3 checksum = Vector3.zero;
                for (int i = 0; i < 128; i++)
                    checksum += step(separate(i % 100, positions[i % 100]), Vector3.forward, .02f, 13f, 19f);
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++)
                {
                    int slot = i % 100;
                    checksum += separate(slot, positions[slot]);
                    checksum += step(Vector3.forward, Vector3.right * 4.2f, .02f, 13f, 19f);
                    bool available = clear(positions[slot], slot, 4f);
                    if (available) checksum.x += .000001f;
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
                Report.AppendLine("MEASURE warmed10000_spacing_velocity_admission_managed_bytes=" + allocated);
                Check(allocated == 0 && !float.IsNaN(checksum.x),
                    "Warmed production spacing/velocity/admission helpers allocate zero managed bytes");
            }
            finally { Object.DestroyImmediate(root); active.SetValue(null, previous); }
        }

        private static float MeanNearestDistance(Vector3[] positions)
        {
            float sum = 0f;
            for (int i = 0; i < positions.Length; i++)
            {
                float nearest = float.PositiveInfinity;
                for (int j = 0; j < positions.Length; j++)
                    if (j != i) nearest = Mathf.Min(nearest, (positions[i] - positions[j]).sqrMagnitude);
                sum += Mathf.Sqrt(nearest);
            }
            return sum / positions.Length;
        }

        private static void VerifyCoverReservations()
        {
            var root = new GameObject("Native two-seeker cover reservation QA");
            var active = Field(typeof(AarisOfflineBRDirector), "activeDirector");
            object previous = active.GetValue(null);
            try
            {
                var director = root.AddComponent<AarisOfflineBRDirector>(); director.enabled = false;
                var roster = (Array)Field(typeof(AarisOfflineBRDirector), "rivals").GetValue(director);
                var track = typeof(AarisOfflineBRDirector).GetMethod("TrackRivalPosition", Instance);
                var seekers = new Component[2];
                var health = new AarisOfflineHealth[2];
                for (int i = 0; i < 2; i++)
                {
                    var actor = new GameObject("Cover seeker " + i); actor.transform.SetParent(root.transform);
                    actor.transform.position = Origin + new Vector3(i == 0 ? -5f : 5f, 0f, 9f);
                    health[i] = actor.AddComponent<AarisOfflineHealth>(); Set(health[i], "current", 200f);
                    seekers[i] = actor.AddComponent(Bot); ((Behaviour)seekers[i]).enabled = false;
                    Set(seekers[i], "owner", director); Set(seekers[i], "health", health[i]);
                    Set(seekers[i], "spatialSlot", i); roster.SetValue(seekers[i], i);
                    track.Invoke(director, new object[] { i, seekers[i], actor.transform.position });
                }
                var wall = new GameObject("Single real shelter wall"); wall.transform.SetParent(root.transform);
                wall.transform.position = Origin + new Vector3(0f, 1f, 10f);
                var box = wall.AddComponent<BoxCollider>(); box.size = new Vector3(5f, 2f, .3f);
                Physics.SyncTransforms();
                var first = Bound<Action<Vector3,float>>(seekers[0], "TrySeekTacticalCover");
                var second = Bound<Action<Vector3,float>>(seekers[1], "TrySeekTacticalCover");
                var available = Bound<Func<Vector3,int,float,bool>>(director, "IsRivalCoverAvailable");
                first(Origin, 10f);
                float expiry = (float)Field(Bot, "coverUntil").GetValue(seekers[0]);
                Vector3 destination = (Vector3)Field(Bot, "coverDestination").GetValue(seekers[0]);
                Check(expiry == 12f && destination.z > wall.transform.position.z,
                    "First seeker reserves genuinely occluding native wall for existing2s lifetime");
                second(Origin, 10f);
                Check((float)Field(Bot, "coverUntil").GetValue(seekers[1]) == 0f &&
                    !available(destination, 1, 11.99f),
                    "Second approaching seeker cannot reserve same4m cover neighbourhood");
                Check(available(destination, 1, expiry),
                    "Cover reservation expires at exact original deadline");
                second(Origin, expiry);
                Check((float)Field(Bot, "coverUntil").GetValue(seekers[1]) == expiry + 2f,
                    "Released cover becomes available to the next real seeker");
                Set(health[1], "dead", true);
                Check(available(destination, 0, expiry + .1f),
                    "Dead seeker cannot retain a logical cover reservation");
            }
            finally { Object.DestroyImmediate(root); active.SetValue(null, previous); }
        }

        private static void VerifyNativeRangedSight()
        {
            var root = new GameObject("Isolated 60m native bot LOS QA");
            try
            {
                var shooter = new GameObject("Rifle shooter"); shooter.transform.SetParent(root.transform);
                shooter.transform.position = Origin; shooter.transform.rotation = Quaternion.identity;
                var bot = shooter.AddComponent(Bot); ((Behaviour)bot).enabled = false;
                var target = new GameObject("60m target"); target.transform.SetParent(root.transform);
                target.transform.position = Origin + Vector3.forward * 60f;
                var body = target.AddComponent<CharacterController>(); body.height = 1.8f; body.radius = .31f; body.center = Vector3.up * .9f;
                // As in the existing native combat QA, Edit Mode LOS uses a
                // real query collider because CC registers fully in Play Mode.
                var query = target.AddComponent<BoxCollider>(); query.size = new Vector3(.62f,1.8f,.62f); query.center = Vector3.up * .9f;
                var hp = target.AddComponent<AarisOfflineHealth>(); Set(hp, "current", 200f);
                var visible = new GameObject("Visible body admission"); visible.transform.SetParent(shooter.transform);
                Set(bot, "visualRoot", visible); Set(bot, "playerTarget", target.transform);
                Set(bot, "playerTargetHealth", hp); Set(bot, "playerTargetCapsule", body);
                Set(bot, "carriedGun", new AarisWeaponSpec { category="AR",rangeMetres=240f });
                Set(bot, "carriedMagazine", 30);
                var selection = Bound<Func<bool>>(bot, "HasPlayerSightForSelection");
                Physics.SyncTransforms();
                Check(selection(), "Native PhysX admits a visible rifle target at60m");
                var wall = new GameObject("Real blocking wall"); wall.transform.SetParent(root.transform);
                wall.transform.position = Origin + Vector3.forward * 30f + Vector3.up;
                var box = wall.AddComponent<BoxCollider>(); box.size = new Vector3(6f, 4f, .3f);
                Physics.SyncTransforms();
                Check(!selection(), "60m rifle perception remains blocked by real wall");
                box.enabled = false; Physics.SyncTransforms();
                Set(bot, "carriedGun", null);
                Check(!selection(), "Unarmed actor cannot reserve distant player threat slot");
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static void VerifyPresentationResources()
        {
            Shader contact = Resources.Load<Shader>("AarisRivalContact");
            Shader readable = Resources.Load<Shader>("AarisRivalReadability");
            Check(contact != null && readable != null, "New shaders are included as Resources assets");
            Check(!ShaderUtil.ShaderHasError(contact) && !ShaderUtil.ShaderHasError(readable),
                "Loaded contact/readability shaders report no native compiler errors");
            Material contactTemplate = Resources.Load<Material>("AarisRivalContactMaterial");
            Material readableTemplate = Resources.Load<Material>("AarisRivalReadabilityMaterial");
            Check(contactTemplate != null && contactTemplate.shader == contact && contactTemplate.enableInstancing,
                "Included contact material retains its shader's GPU instancing build variants");
            Check(readableTemplate != null && readableTemplate.shader == readable && readableTemplate.enableInstancing,
                "Included distant material retains its shader's GPU instancing build variants");
            Report.AppendLine("INFO InstancingSupported=" + SystemInfo.supportsInstancing +
                "; full rendering and Android variants require the separate native render gate");
        }
    }
}
#endif
