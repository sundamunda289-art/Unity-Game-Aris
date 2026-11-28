#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>Matched static-world GL draw evidence, not gameplay/FPS evidence.
    /// Dedicated disposable batch Editor only; never saves a source asset.</summary>
    [InitializeOnLoad]
    public static class AarisUltra60FixedViewCapture
    {
        private const string Session = "Aaris.Ultra60.FixedViews.V1";
        private const string MainScene = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const int Width = 480, Height = 270, WarmRenders = 3, MeasuredRenders = 3;
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string Output = OutputDirectory();
        private static readonly bool Trace = Environment.GetEnvironmentVariable("AARIS_ULTRA60_TRACE") == "1";
        private static readonly string[] Names = { "aerial", "canal_market", "clock_tower", "forest" };
        private static readonly Vector2[] GroundPoints = { Vector2.zero,
            new Vector2(-118f, 600f), new Vector2(350f, 170f), new Vector2(-650f, -170f) };
        private static readonly float[] TargetZ = { 250f, 680f, 260f, -100f };
        private static readonly List<PoseReport> completed = new List<PoseReport>(4);
        private static AarisThirdPersonPlayer player;
        private static AarisOfflineDrop drop;
        private static AarisOfflineBRDirector director;
        private static Camera camera;
        private static RenderTexture target;
        private static IntPtr callback;
        private static Report report;
        private static PoseReport pose;
        private static int poseIndex, state, lastFrame = -1, warm;
        private static double poseStarted, stableSince, nextInitializationStatus;
        private static string stableScenes;
        private static bool stopping;
        private static FieldInfo phaseField, controlsField, clearanceField, pendingSceneField;

        [DllImport("/tmp/aaris-ultra60/trace/libaaris_render_markers.so", EntryPoint = "GetAarisTraceMarker",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetAarisTraceMarker();

        [Serializable] public sealed class TerrainInfo
        {
            public string name, shader, shadowMode;
            public int layers, alphamapTextures, alphamapResolution, treeInstances, detailWidth, detailHeight;
            public bool drawInstanced, drawHeightmap, drawFoliage;
            public float basemapDistance, detailDistance, treeDistance, billboardDistance;
            public float heightmapPixelError;
        }
        [Serializable] public sealed class Census
        {
            public int activeRenderers, activeSkinnedRenderers, activeAnimators, spawnedBots;
            public int enabledRendererComponents, effectiveEnabledRenderers, forceSuppressedRenderers;
            public int bakedStaticCohorts = -1, bakedActiveCombinedRenderers = -1,
                bakedReportedActiveCombinedRenderers = -1, bakedSuppressedSourceRenderers = -1,
                bakedRejectedCohorts = -1;
            public int uniqueMaterials, uniqueShaders, nullMaterialSlots, unsupportedShaders,
                missingShaders, errorShaders, compilerErrorShaders, missingScriptComponents;
            public int staticSectorLods = -1, macroSectorLods = -1, batchedSectorRenderers = -1,
                unreadableSectorRenderers = -1;
            public string[] shaderIssues;
            public TerrainInfo[] terrains;
        }
        [Serializable] public sealed class PoseReport
        {
            public string name, status = "capturing", screenshot;
            public bool flightMacroFixture, streamingSettled, settleTimedOut, pendingSceneOperationAtCapture,
                sceneListChangedDuringCapture, rendererCountChangedDuringCapture;
            public double settleWallSeconds;
            public int phaseOrdinal, warmupRenders, measuredRenders, firstBeginMarker, lastEndMarker;
            public Vector3 playerPosition, cameraPosition, cameraLookAt;
            public Quaternion cameraRotation;
            public float fieldOfView, cameraAspect, nearClip, farClip, terrainHeight, unityTime;
            public int cullingMask;
            public string[] loadedScenesAtCapture, loadedScenesAfterCapture;
            public double[] cameraRenderCallMilliseconds = new double[MeasuredRenders];
            public float screenshotPixelVariation, screenshotMagentaFraction;
            public Census before, after;
        }
        [Serializable] public sealed class Report
        {
            public string status = "starting", reason = "", unityVersion, graphicsAPI, graphicsDevice;
            public string scope = "Matched fixed-camera STATIC WORLD draw supplement in real Editor Play Mode. Not Android FPS, bot gameplay, driving, or a full-frame draw budget.";
            public string fixture = "Existing bot director disabled and coroutines stopped before Begin; zero bot spawns required. Existing player/drop locomotion and native player capsule disabled; authored aircraft/canopy hidden. Aerial uses InPlane/controlsSuspended; ground uses Landed. World streamers, terrain, loot, vehicles and settlement systems continue naturally. No source asset is saved.";
            public string settleScope = "Loaded scene path list unchanged for at least one wall second and no incomplete AsyncOperation in any live AarisAdditiveChunkStreamer.inFlight, with a ten-second wall limit. This cannot certify arbitrary ResourceRequest/background imports.";
            public string traceEncoding = "begin=phaseOrdinal*100000+sampleNumber*2, end=begin+1, sampleNumber 1..3. Markers bracket only measured Camera.Render. Three earlier warmup renders are unmarked. PNG reads the final measured target without another render.";
            public string timingScope = "Camera.Render call wall time only; not GPU completion, not a frame-time/FPS benchmark. Native trace instrumentation adds overhead.";
            public string instrumentationRevision = "boundary_cohort_census_v1; camera poses, render loop, markers and timing are unchanged from fixed harness 9dddc434954fb62cf4973661e2fce860073ae07693b5d61265771a0eef823f4e";
            public string censusScope = "Unmeasured boundaries. Existing activeRenderers and explicit effectiveEnabledRenderers both require enabled, activeInHierarchy and !forceRenderingOff. Raw enabled and forced-off counts are separate. Baked active combined outputs are counted directly; reported active/suppressed/rejected/cohort values are owner counters. Missing production types/counters are -1.";
            public bool traceEnabled = Trace, android60FpsVerified = false;
            public int width = Width, height = Height, runtimeErrors, shaderErrorLogs;
            public string firstError = "", pipeline;
            public string rendererDataName = "unavailable", rendererDataPath = "unavailable",
                rendererDepthPrimingName = "unavailable";
            public int rendererDataIndex = -1, rendererDepthPrimingMode = -1, cameraRendererOverride = -1;
            public bool hdr, cameraHDR, srpBatcher, depthTexture, opaqueTexture;
            public int msaa, shadowCascades, shadowAtlas;
            public float renderScale, shadowDistance;
            public PoseReport[] poses;
        }

        static AarisUltra60FixedViewCapture()
        {
            EditorApplication.update += Tick;
            Application.logMessageReceived += OnLog;
        }
        private static string OutputDirectory()
        {
            string value = Environment.GetEnvironmentVariable("AARIS_ULTRA60_FIXED_REPORT_DIR");
            string path = Path.GetFullPath(string.IsNullOrEmpty(value) ? "/tmp/aaris-ultra60-fixed-views" : value);
            if (!path.StartsWith("/tmp/", StringComparison.Ordinal)) throw new InvalidOperationException("FIXED_VIEW_OUTPUT_MUST_BE_TMP");
            return path;
        }
        [MenuItem("Aaris/CI/Capture matched static world views")]
        public static void Run()
        {
            if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("FIXED_VIEW_REQUIRES_IDLE_BATCH_EDITOR");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("UNSAVED_SCENE");
            Directory.CreateDirectory(Output);
            SessionState.SetString(Session, "load");
            SessionState.SetInt(Session + ".errors", 0); SessionState.SetInt(Session + ".shaderErrors", 0);
            SessionState.SetString(Session + ".firstError", "");
            SessionState.SetFloat(Session + ".deadline", (float)EditorApplication.timeSinceStartup + 600f);
            Status("starting fixed-view capture");
        }
        private static void OnLog(string message, string stack, LogType type)
        {
            if (string.IsNullOrEmpty(SessionState.GetString(Session, "")) ||
                (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            SessionState.SetInt(Session + ".errors", SessionState.GetInt(Session + ".errors", 0) + 1);
            if (message != null && message.IndexOf("shader", StringComparison.OrdinalIgnoreCase) >= 0)
                SessionState.SetInt(Session + ".shaderErrors", SessionState.GetInt(Session + ".shaderErrors", 0) + 1);
            if (string.IsNullOrEmpty(SessionState.GetString(Session + ".firstError", "")))
                SessionState.SetString(Session + ".firstError", type.ToString());
        }
        private static void Tick()
        {
            string session = SessionState.GetString(Session, "");
            if (string.IsNullOrEmpty(session)) return;
            if (session == "stop")
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                int code = SessionState.GetInt(Session + ".exit", 1);
                string path = Path.Combine(Output, "report.json");
                if (File.Exists(path))
                {
                    Report final = JsonUtility.FromJson<Report>(File.ReadAllText(path));
                    final.runtimeErrors = SessionState.GetInt(Session + ".errors", 0);
                    final.shaderErrorLogs = SessionState.GetInt(Session + ".shaderErrors", 0);
                    final.firstError = SessionState.GetString(Session + ".firstError", "");
                    if (final.runtimeErrors > 0) { final.status = "failed_or_incomplete"; code = 1; }
                    File.WriteAllText(path, JsonUtility.ToJson(final, true));
                }
                SessionState.SetString(Session, ""); EditorApplication.Exit(code); return;
            }
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Session + ".deadline", 0f))
            { Finish(false, "FIXED_VIEW_600_SECOND_DEADLINE"); return; }
            try
            {
                if (session == "load")
                {
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                    Status("scene_opening");
                    EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
                    Status("scene_opened_waiting_play");
                    SessionState.SetString(Session, "play"); EditorApplication.isPlaying = true; return;
                }
                if (!EditorApplication.isPlaying || Time.frameCount == 0 || Time.frameCount == lastFrame) return;
                lastFrame = Time.frameCount;
                if (report == null) { Initialize(); return; }
                if (SessionState.GetInt(Session + ".errors", 0) > 0)
                { Finish(false, "UNITY_ERROR_DURING_FIXED_CAPTURE"); return; }
                ApplyPose();
                if (state == 1) PollSettling();
                else if (state == 2)
                {
                    Render(false); pose.warmupRenders++;
                    if (++warm >= WarmRenders)
                    {
                        pose.loadedScenesAtCapture = LoadedScenes();
                        pose.pendingSceneOperationAtCapture = PendingSceneOperation();
                        pose.before = TakeCensus();
                        state = 3; Status(pose.name + " measuring");
                    }
                }
                else if (state == 3)
                {
                    Render(true);
                    if (pose.measuredRenders >= MeasuredRenders) CompletePose();
                }
            }
            catch (Exception ex)
            { Finish(false, "HARNESS_" + ex.GetType().Name + (ex.Message.Length < 120 ? "_" + ex.Message : "")); }
        }
        private static void Initialize()
        {
            player = Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
            director = Object.FindFirstObjectByType<AarisOfflineBRDirector>();
            drop = player != null ? player.GetComponent<AarisOfflineDrop>() : null;
            camera = player != null ? player.AimCamera : null;
            if (player == null || director == null || drop == null || camera == null || !(bool)Read(drop, "planeReady"))
            {
                if (EditorApplication.timeSinceStartup >= nextInitializationStatus)
                { nextInitializationStatus = EditorApplication.timeSinceStartup + 1d; Status("waiting_required_player_director_drop_camera_and_completed_drop_start"); }
                return;
            }
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) throw new InvalidOperationException("NULL_GRAPHICS");
            if ((bool)Read(director, "started") || (int)Read(director, "spawnedRivals") != 0)
                throw new InvalidOperationException("BOT_DIRECTOR_ALREADY_BEGAN");
            director.enabled = false; director.StopAllCoroutines();
            phaseField = Field(drop.GetType(), "phase"); controlsField = Field(drop.GetType(), "controlsSuspended");
            clearanceField = Field(drop.GetType(), "sampledGroundClearance");
            // Normal OnDisable restores the walking camera/motor and body.
            // The disclosed fixed fixture owns those authorities instead.
            controlsField.SetValue(drop, false);
            drop.enabled = false; drop.StopAllCoroutines(); player.enabled = false;
            CharacterController capsule = player.GetComponent<CharacterController>();
            if (capsule != null) capsule.enabled = false;
            GameObject plane = Read(drop, "plane") as GameObject, canopy = Read(drop, "canopy") as GameObject;
            if (plane != null) plane.SetActive(false); if (canopy != null) canopy.SetActive(false);
            pendingSceneField = Field(typeof(AarisAdditiveChunkStreamer), "inFlight");
            if (Trace) { callback = GetAarisTraceMarker(); if (callback == IntPtr.Zero) throw new InvalidOperationException("TRACE_CALLBACK_MISSING"); }
            target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32); target.Create();
            report = new Report { unityVersion = Application.unityVersion,
                graphicsAPI = SystemInfo.graphicsDeviceType.ToString(), graphicsDevice = SystemInfo.graphicsDeviceName };
            completed.Clear(); poseIndex = 0; BeginPose();
        }
        private static void BeginPose()
        {
            bool aerial = poseIndex == 0;
            Vector2 point = GroundPoints[poseIndex];
            float height = GroundHeight(point.x, point.y);
            Vector3 eye = aerial ? new Vector3(0f, 600f, 0f) : new Vector3(point.x, height + 3f, point.y);
            Vector3 look = aerial ? new Vector3(0f, 0f, 250f) : new Vector3(point.x, height + 3f, TargetZ[poseIndex]);
            pose = new PoseReport { name = Names[poseIndex], phaseOrdinal = poseIndex + 1,
                flightMacroFixture = aerial, terrainHeight = height, cameraPosition = eye, cameraLookAt = look,
                cameraRotation = Quaternion.LookRotation(look - eye, Vector3.up),
                playerPosition = aerial ? eye : new Vector3(point.x, height + .25f, point.y), fieldOfView = 60f };
            phaseField.SetValue(drop, aerial ? AarisOfflineDrop.DropPhase.InPlane : AarisOfflineDrop.DropPhase.Landed);
            controlsField.SetValue(drop, aerial); clearanceField.SetValue(drop, aerial ? 600f : 0f);
            ApplyPose(); Physics.SyncTransforms();
            poseStarted = stableSince = EditorApplication.timeSinceStartup;
            stableScenes = string.Join("|", LoadedScenes()); warm = 0; state = 1;
            Status(pose.name + " settling");
        }
        private static void ApplyPose()
        {
            player.transform.position = pose.playerPosition;
            player.transform.rotation = Quaternion.Euler(0f, pose.cameraRotation.eulerAngles.y, 0f);
            camera.transform.SetPositionAndRotation(pose.cameraPosition, pose.cameraRotation);
            camera.orthographic = false; camera.fieldOfView = 60f; camera.aspect = Width / (float)Height;
            pose.cameraAspect = camera.aspect; pose.nearClip = camera.nearClipPlane; pose.farClip = camera.farClipPlane;
            pose.cullingMask = camera.cullingMask;
        }
        private static void PollSettling()
        {
            double now = EditorApplication.timeSinceStartup;
            string signature = string.Join("|", LoadedScenes());
            bool pending = PendingSceneOperation();
            if (signature != stableScenes || pending) { stableScenes = signature; stableSince = now; }
            bool ready = !pending && now - stableSince >= 1d;
            if (!ready && now - poseStarted < 10d) return;
            pose.streamingSettled = ready; pose.settleTimedOut = !ready;
            pose.settleWallSeconds = now - poseStarted; state = 2;
            Status(pose.name + (ready ? " warming_settled" : " warming_UNSETTLED"));
        }
        private static bool PendingSceneOperation()
        {
            var streamers = Object.FindObjectsByType<AarisAdditiveChunkStreamer>(FindObjectsSortMode.None);
            for (int i = 0; i < streamers.Length; i++)
            {
                AsyncOperation operation = pendingSceneField.GetValue(streamers[i]) as AsyncOperation;
                if (operation != null && !operation.isDone) return true;
            }
            return false;
        }
        private static string[] LoadedScenes()
        {
            var paths = new List<string>(SceneManager.sceneCount);
            for (int i = 0; i < SceneManager.sceneCount; i++)
            { Scene scene = SceneManager.GetSceneAt(i); if (scene.isLoaded) paths.Add(scene.path); }
            paths.Sort(StringComparer.Ordinal); return paths.ToArray();
        }
        private static void Render(bool measured)
        {
            ApplyPose(); // No LateUpdate/camera owner can alter the actual sampled view.
            RenderTexture previous = camera.targetTexture, active = RenderTexture.active;
            int begin = pose.phaseOrdinal * 100000 + (pose.measuredRenders + 1) * 2;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Trace && measured) GL.IssuePluginEvent(callback, begin);
            try { camera.targetTexture = target; camera.Render(); }
            finally
            {
                camera.targetTexture = previous; RenderTexture.active = active;
                if (Trace && measured) GL.IssuePluginEvent(callback, begin + 1);
            }
            if (!measured) return;
            pose.cameraRenderCallMilliseconds[pose.measuredRenders] =
                (System.Diagnostics.Stopwatch.GetTimestamp() - started) * (1000d / System.Diagnostics.Stopwatch.Frequency);
            if (pose.measuredRenders == 0) pose.firstBeginMarker = begin;
            pose.lastEndMarker = begin + 1; pose.measuredRenders++;
        }
        private static void CompletePose()
        {
            Status(pose.name + " screenshot_readback");
            SaveScreenshot();
            pose.loadedScenesAfterCapture = LoadedScenes();
            pose.sceneListChangedDuringCapture = string.Join("|", pose.loadedScenesAtCapture) != string.Join("|", pose.loadedScenesAfterCapture);
            pose.after = TakeCensus(); pose.unityTime = Time.time;
            pose.rendererCountChangedDuringCapture = pose.before.activeRenderers != pose.after.activeRenderers;
            pose.status = pose.streamingSettled && !pose.pendingSceneOperationAtCapture &&
                !pose.sceneListChangedDuringCapture && !pose.rendererCountChangedDuringCapture ? "captured_stable" : "captured_unsettled";
            if (pose.after.spawnedBots != 0) throw new InvalidOperationException("STATIC_FIXTURE_SPAWNED_BOTS");
            completed.Add(pose); pose = null; Save();
            if (++poseIndex < Names.Length) BeginPose();
            else
            {
                bool stable = true;
                for (int i = 0; i < completed.Count; i++) stable &= completed[i].status == "captured_stable";
                Finish(stable, stable ? "FOUR_MATCHED_STATIC_VIEWS_CAPTURED" : "UNSETTLED_VIEW_REQUIRES_REVIEW");
            }
        }
        private static void SaveScreenshot()
        {
            RenderTexture prior = RenderTexture.active;
            var texture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = target; texture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0); texture.Apply(false, false);
                Color32[] pixels = texture.GetPixels32(); double total = 0d, squares = 0d; int magenta = 0;
                for (int i = 0; i < pixels.Length; i++)
                { Color32 p = pixels[i]; double grey = (p.r + p.g + p.b) / 3d; total += grey; squares += grey * grey;
                    if (p.r > 190 && p.b > 190 && p.g < 75) magenta++; }
                double mean = total / pixels.Length;
                pose.screenshotPixelVariation = (float)Math.Sqrt(Math.Max(0d, squares / pixels.Length - mean * mean));
                pose.screenshotMagentaFraction = (float)magenta / pixels.Length;
                pose.screenshot = pose.name + ".png";
                File.WriteAllBytes(Path.Combine(Output, pose.screenshot), texture.EncodeToPNG());
            }
            finally { RenderTexture.active = prior; Object.DestroyImmediate(texture); }
        }
        private static float GroundHeight(float x, float z)
        {
            foreach (Terrain terrain in Terrain.activeTerrains)
            {
                if (terrain == null || terrain.terrainData == null) continue;
                Vector3 origin = terrain.transform.position, size = terrain.terrainData.size;
                if (x < origin.x || z < origin.z || x > origin.x + size.x || z > origin.z + size.z) continue;
                return origin.y + terrain.SampleHeight(new Vector3(x, 0f, z));
            }
            throw new InvalidOperationException("FIXED_POSE_OUTSIDE_NATIVE_TERRAIN");
        }
        private static Census TakeCensus()
        {
            var c = new Census { spawnedBots = (int)Read(director, "spawnedRivals") };
            var materials = new HashSet<Material>(); var shaders = new HashSet<Shader>(); var issues = new List<string>();
            foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (renderer.enabled && renderer.gameObject.activeInHierarchy)
                { c.enabledRendererComponents++; if (renderer.forceRenderingOff) c.forceSuppressedRenderers++; }
                if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy) continue;
                c.activeRenderers++; if (renderer is SkinnedMeshRenderer) c.activeSkinnedRenderers++;
                foreach (Material material in renderer.sharedMaterials)
                    if (material == null) c.nullMaterialSlots++; else materials.Add(material);
            }
            foreach (Animator animator in Object.FindObjectsByType<Animator>(FindObjectsSortMode.None))
                if (animator.isActiveAndEnabled) c.activeAnimators++;
            Terrain[] terrains = Terrain.activeTerrains; c.terrains = new TerrainInfo[terrains.Length];
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain terrain = terrains[i]; TerrainData data = terrain.terrainData; Material material = terrain.materialTemplate;
                if (material != null) materials.Add(material);
                c.terrains[i] = new TerrainInfo { name = terrain.name, shader = material != null && material.shader != null ? material.shader.name : "unavailable",
                    shadowMode = terrain.shadowCastingMode.ToString(), layers = data != null ? data.terrainLayers.Length : -1,
                    alphamapTextures = data != null ? data.alphamapTextureCount : -1, alphamapResolution = data != null ? data.alphamapResolution : -1,
                    treeInstances = data != null ? data.treeInstanceCount : -1, detailWidth = data != null ? data.detailWidth : -1,
                    detailHeight = data != null ? data.detailHeight : -1, drawInstanced = terrain.drawInstanced,
                    drawHeightmap = terrain.drawHeightmap, drawFoliage = terrain.drawTreesAndFoliage,
                    basemapDistance = terrain.basemapDistance, detailDistance = terrain.detailObjectDistance,
                    treeDistance = terrain.treeDistance, billboardDistance = terrain.treeBillboardDistance,
                    heightmapPixelError = terrain.heightmapPixelError };
            }
            c.uniqueMaterials = materials.Count;
            foreach (Material material in materials)
            { if (material.shader == null) { c.missingShaders++; if (issues.Count < 32) issues.Add("missing_shader " + material.name); }
                else shaders.Add(material.shader); }
            c.uniqueShaders = shaders.Count;
            foreach (Shader shader in shaders)
            {
                if (!shader.isSupported) { c.unsupportedShaders++; if (issues.Count < 32) issues.Add("unsupported " + shader.name); }
                if (shader.name.IndexOf("InternalErrorShader", StringComparison.OrdinalIgnoreCase) >= 0) c.errorShaders++;
                if (ShaderUtil.ShaderHasError(shader)) { c.compilerErrorShaders++; if (issues.Count < 32) issues.Add("compiler_error " + shader.name); }
            }
            c.shaderIssues = issues.ToArray();
            foreach (GameObject item in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (item.scene.IsValid() && item.scene.isLoaded) c.missingScriptComponents += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(item);
            Type type = typeof(AarisOfflineBRDirector).Assembly.GetType("Aaris.MapReview.AarisWorldSectorRuntime");
            Object sectors = type == null ? null : Object.FindFirstObjectByType(type);
            if (sectors != null)
            {
                c.staticSectorLods = Counter(sectors, "StaticLodCount"); c.macroSectorLods = Counter(sectors, "MacroLodCount");
                c.batchedSectorRenderers = Counter(sectors, "BatchedRendererCount"); c.unreadableSectorRenderers = Counter(sectors, "UnreadableRendererCount");
            }
            c.effectiveEnabledRenderers = c.activeRenderers;
            ReadBakedBatchCounters(c);
            return c;
        }
        private static void ReadBakedBatchCounters(Census census)
        {
            Type type = typeof(AarisOfflineBRDirector).Assembly.GetType("Aaris.MapReview.AarisBakedStaticBatch");
            if (type == null) return;
            census.bakedStaticCohorts = StaticCounter(type, "RegisteredCohortCount");
            census.bakedReportedActiveCombinedRenderers = StaticCounter(type, "TotalActiveCombinedRenderers");
            census.bakedSuppressedSourceRenderers = StaticCounter(type, "TotalSuppressedSourceRenderers");
            census.bakedRejectedCohorts = StaticCounter(type, "TotalRejectedCohorts");
            FieldInfo outputs = type.GetField("outputs", BindingFlags.Instance | BindingFlags.NonPublic);
            if (outputs == null) return;
            var unique = new HashSet<Renderer>();
            census.bakedActiveCombinedRenderers = 0;
            foreach (Object cohort in Object.FindObjectsByType(type, FindObjectsSortMode.None))
            {
                Renderer[] renderers = outputs.GetValue(cohort) as Renderer[];
                if (renderers == null) { census.bakedActiveCombinedRenderers = -1; return; }
                foreach (Renderer renderer in renderers)
                    if (renderer != null && renderer.enabled && !renderer.forceRenderingOff &&
                        renderer.gameObject.activeInHierarchy && unique.Add(renderer))
                        census.bakedActiveCombinedRenderers++;
            }
        }
        private static int StaticCounter(Type type, string name)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            return property != null && property.PropertyType == typeof(int) ? (int)property.GetValue(null) : -1;
        }
        private static int Counter(Object value, string name)
        { PropertyInfo p = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance); return p != null && p.PropertyType == typeof(int) ? (int)p.GetValue(value) : -1; }
        private static FieldInfo Field(Type type, string name) => type.GetField(name, Members) ?? throw new InvalidOperationException("MISSING_FIELD_" + name);
        private static object Read(object value, string name) => Field(value.GetType(), name).GetValue(value);
        private static void Status(string text) => File.WriteAllText(Path.Combine(Output, "status.txt"), text + "\n");
        private static void Save()
        {
            if (report == null) report = new Report { unityVersion = Application.unityVersion };
            report.runtimeErrors = SessionState.GetInt(Session + ".errors", 0);
            report.shaderErrorLogs = SessionState.GetInt(Session + ".shaderErrors", 0);
            report.firstError = SessionState.GetString(Session + ".firstError", "");
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline != null)
            {
                report.pipeline = pipeline.name; report.hdr = pipeline.supportsHDR; report.msaa = pipeline.msaaSampleCount;
                report.renderScale = pipeline.renderScale; report.shadowDistance = pipeline.shadowDistance;
                report.shadowCascades = pipeline.shadowCascadeCount; report.shadowAtlas = pipeline.mainLightShadowmapResolution;
                report.srpBatcher = pipeline.useSRPBatcher; report.depthTexture = pipeline.supportsCameraDepthTexture;
                report.opaqueTexture = pipeline.supportsCameraOpaqueTexture;
                ReadRendererDataMetadata(report, pipeline, camera);
            }
            report.cameraHDR = camera != null && camera.allowHDR; report.poses = completed.ToArray();
            File.WriteAllText(Path.Combine(Output, "report.json"), JsonUtility.ToJson(report, true));
            File.WriteAllText(Path.Combine(Output, "trace-markers.json"), JsonUtility.ToJson(report, true));
        }
        private static void ReadRendererDataMetadata(Report target, UniversalRenderPipelineAsset pipeline, Camera activeCamera)
        {
            // Editor-only boundary metadata. Resolve the camera override if it
            // exists; do not assume every camera uses the asset's default renderer.
            var pipelineState = new SerializedObject(pipeline);
            SerializedProperty list = pipelineState.FindProperty("m_RendererDataList");
            SerializedProperty defaultIndex = pipelineState.FindProperty("m_DefaultRendererIndex");
            if (list == null || defaultIndex == null) return;
            int index = defaultIndex.intValue;
            target.cameraRendererOverride = -1;
            UniversalAdditionalCameraData cameraData = activeCamera == null ? null :
                activeCamera.GetComponent<UniversalAdditionalCameraData>();
            if (cameraData != null)
            {
                var cameraState = new SerializedObject(cameraData);
                SerializedProperty rendererIndex = cameraState.FindProperty("m_RendererIndex");
                if (rendererIndex != null)
                {
                    target.cameraRendererOverride = rendererIndex.intValue;
                    if (rendererIndex.intValue >= 0) index = rendererIndex.intValue;
                }
            }
            if (index < 0 || index >= list.arraySize) return;
            Object data = list.GetArrayElementAtIndex(index).objectReferenceValue;
            if (data == null) return;
            target.rendererDataIndex = index;
            target.rendererDataName = data.name;
            target.rendererDataPath = AssetDatabase.GetAssetPath(data);
            var rendererState = new SerializedObject(data);
            SerializedProperty priming = rendererState.FindProperty("m_DepthPrimingMode");
            if (priming == null) return;
            target.rendererDepthPrimingMode = priming.intValue;
            target.rendererDepthPrimingName = priming.intValue == 0 ? "Disabled" :
                priming.intValue == 1 ? "Auto" : priming.intValue == 2 ? "Forced" : "unknown";
        }

        private static void Finish(bool success, string reason)
        {
            if (stopping) return; stopping = true;
            try
            {
                if (pose != null) { pose.status = "incomplete"; completed.Add(pose); pose = null; }
                if (report == null) report = new Report();
                success &= SessionState.GetInt(Session + ".errors", 0) == 0;
                report.status = success ? "completed_static_world_capture" : "failed_or_incomplete";
                report.reason = reason; Save(); Status(report.status + " " + reason);
            }
            finally
            {
                if (target != null) { target.Release(); Object.DestroyImmediate(target); target = null; }
                SessionState.SetInt(Session + ".exit", success ? 0 : 1); SessionState.SetString(Session, "stop");
                if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            }
        }
    }
}
#endif
