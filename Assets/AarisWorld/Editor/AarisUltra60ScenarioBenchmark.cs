#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Aaris.MapReview.Editor
{
    /// <summary>
    /// Dedicated batch Editor scenario capture of the actual authored map.
    /// Never saves an asset or scene. Editor / software-GL evidence is explicitly
    /// separated from Android acceptance. All output remains under /tmp.
    /// </summary>
    [InitializeOnLoad]
    public static class AarisUltra60ScenarioBenchmark
    {
        private const string Session = "Aaris.Ultra60Scenario.V1";
        private const string ScenePath = "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity";
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int HistogramBins = 16001; // 0.25 ms, overflow >= 4 seconds.
        private const int WarmFrames = 8;
        private static readonly string Output = ReadOutput();
        private static readonly float SampleSeconds = ReadSeconds();
        private static readonly int MinimumSamples = ReadMinimumSamples();
        private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("AARIS_ULTRA60_TRACE") == "1";
        private static readonly bool FunctionalTransit = Environment.GetEnvironmentVariable("AARIS_ULTRA60_FUNCTIONAL_TRANSIT") == "1";
        private const int MeasuredRenderWidth = 480, MeasuredRenderHeight = 270;
        private static Report report;
        private static readonly List<PhaseReport> completed = new List<PhaseReport>(12);
        private static PhaseReport current;
        private static int[] histogram;
        private static int stage, warmup, lastFrame = -1, driveAttempt;
        private static float phaseBegan, stageBegan, nextSetupAt, drivePeakSpeed;
        private static double nextInitializationDiagnostic;
        private static double phaseGameBegan, phaseFixedBegan;
        private static Vector3 driveOrigin;
        private static AarisThirdPersonPlayer player;
        private static AarisOfflineDrop drop;
        private static AarisOfflineBRDirector director;
        private static AarisOfflineHealth health;
        private static CharacterController playerCapsule;
        private static AarisVehicleSession vehicleSession;
        private static AarisVehicleMotor selectedCar;
        // Setup-only state: the natural landing can be outside the105m car
        // spawn radius. Observe real plans; never create or move a vehicle.
        private static AarisVehicleWorld drivingWorld;
        private static AarisNearbyColliders drivingNearbyColliders;
        private static MethodInfo drivingInactiveObstacleQuery;
        private static float nextDrivingPlanApproach;
        private static readonly HashSet<object> approachedDrivingPlans = new HashSet<object>();
        private static readonly HashSet<int> exhaustedDrivingCars = new HashSet<int>();
        private static readonly Collider[] drivingPlacementOverlaps = new Collider[64];
        private static ProfilerRecorder main, draws, setPass, gc;
        private static RenderTexture measuredTarget;
        private static bool previousFrameRendered;
        private static int previousRenderedFrame;
        private static IntPtr traceEvent;

        [DllImport("/tmp/aaris-ultra60/trace/libaaris_render_markers.so", EntryPoint = "GetAarisTraceMarker",
            CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GetAarisTraceMarker();
        private static bool stopping;
        private static int damageEvents, combatDamageStart;
        private static readonly Component[] combatants = new Component[6];
        private static readonly int[] combatAmmoStart = new int[6];
        private static FieldInfo rosterField, spawnedField, aliveField, spawnCompleteField;
        private static readonly RaycastHit[] groundHits = new RaycastHit[64];

        [Serializable]
        public sealed class Metric
        {
            public int samples, unavailableFrames;
            public double sum, mean = -1, minimum = -1, maximum = -1;
            public string unavailableReason = "";
            public void Add(double value)
            {
                if (value < 0 || double.IsNaN(value) || double.IsInfinity(value)) { unavailableFrames++; return; }
                if (samples++ == 0) minimum = maximum = value;
                else { minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value); }
                sum += value;
            }
            public void Finish() { if (samples > 0) mean = sum / samples; }
        }

        [Serializable]
        public sealed class Census
        {
            public int activeRendererComponents, activeSkinnedRenderers, activeAnimatorComponents;
            public int enabledRendererComponents, effectiveEnabledRenderers, forceSuppressedRenderers;
            public int bakedStaticCohorts = -1, bakedActiveCombinedRenderers = -1,
                bakedReportedActiveCombinedRenderers = -1, bakedSuppressedSourceRenderers = -1,
                bakedRejectedCohorts = -1;
            public int loadedScenes, activeTerrains, spawnedRivals = -1, aliveRivals = -1;
            public int uniqueActiveMaterials, uniqueActiveShaders, nullMaterialSlots, missingShaders;
            public int unsupportedShaders, internalErrorShaders, shadersWithCompilerErrors, missingBehaviourComponents;
            public bool activeShaderCensusPassed;
            public string[] shaderIssues;
            public int sectorStaticLods = -1, sectorMacroLods = -1, sectorBatchedRenderers = -1,
                sectorUnreadableRenderers = -1;
            public TerrainMetadata[] terrains;
        }

        [Serializable]
        public sealed class TerrainMetadata
        {
            public string name, materialShader, shadowCastingMode;
            public int terrainLayers, alphamapResolution, alphamapTextures, detailWidth, detailHeight;
            public int treeInstances, treePrototypes;
            public bool drawHeightmap, drawTreesAndFoliage, drawInstanced;
            public float basemapDistance, detailDistance, treeDistance, treeBillboardDistance;
            public float heightmapPixelError;
        }

        [Serializable]
        public sealed class DrivingSetupSample
        {
            public string action, selectedVehicle = "", nearbyVehicle = "", planId = "", supportCollider = "";
            public int discoveredMotorComponents, enabledMotors, safeMotors, sideAttempt, groundedWheels;
            public float elapsedSeconds, timeScale, distanceMetres = -1f, speedMetresPerSecond = -1f,
                angularSpeed = -1f;
            public bool footActive, capsuleEnabled, sessionActive, sessionConfigured, canEnter,
                selectedActive, selectedSafe, selectedOccupied, bodyKinematic,
                supportFound, placementClear, entryAttempted, entrySucceeded;
            public Vector3 playerPosition, carPosition, setupPosition;
        }

        [Serializable]
        public sealed class PhaseReport
        {
            public string name, status = "running", reason = "";
            public int frames, warmupFramesExcluded, over16_67ms, over33_33ms, over50ms, over100ms;
            public int explicitRenderSamples, skippedExplicitRenderFrames;
            public double gameElapsedSeconds, fixedElapsedSeconds;
            public int tracePhaseOrdinal;
            public double sampledSeconds, fps = -1, p50ms = -1, p95ms = -1, p99ms = -1, worstMs;
            public bool percentileOverflow;
            public Metric mainThreadNanoseconds = new Metric();
            public Metric drawCalls = new Metric();
            public Metric setPassCalls = new Metric();
            public Metric gcBytes = new Metric();
            public Metric synchronousCameraRenderMilliseconds = new Metric();
            public Census startCensus, endCensus;
            public string screenshot = "", screenshotObservedDropPhase = "", screenshotStatus = "not captured";
            public float screenshotPixelVariation, screenshotMagentaFraction;
        }

        [Serializable]
        public sealed class Report
        {
            public string status = "running", reason = "", unityVersion, graphicsAPI, graphicsDevice;
            public string measurementScope = "Batch Editor with actual game map; not Android device FPS";
            public string renderingScope = "One explicit Camera.Render to a reusable 480x270 target per sampled engine frame. Camera.Render call wall time is reported separately, not claimed as GPU completion time. Frame intervals include the preceding explicit render plus Editor overhead; they are not Android frame times.";
            public bool nativeTraceEnabled = TraceEnabled;
            public bool functionalTransit = FunctionalTransit;
            public int skippedTransitFrames, skippedFunctionalCompletionFrames;
            public string functionalTransitScope = "Opt-in only: AARIS_ULTRA60_FUNCTIONAL_TRANSIT=1 stops explicit camera renders after the first four canopy_to_landing renders. Driving and combat keep the first complete consecutive rendered sample window, then skip further explicit renders while natural input, Update and FixedUpdate continue. Driving completes only after at least the requested sample duration in actual fixedTime; combat requires at least max(10, sample duration) in actual Time.time. Existing gameplay assertions and the global deadline remain unchanged. Per-phase game/fixed elapsed seconds and skipped-render ticks are reported; skipped ticks have no render, frame-time or FPS claim. Default false and all other phases keep the original rendering path.";
            public string counterScope = "Completed-frame ProfilerRecorder values. Missing/wrong-unit counters are -1/unavailable. All-zero draw or SetPass counters despite explicit camera renders are invalidated as unavailable; native GL trace is required for actual API draw counts.";
            public string censusScope = "Active enabled renderer/animator components at phase boundaries; not submitted draws or animation jobs.";
            public string instrumentationRevision = "boundary_cohort_census_v4 plus opt-in natural-time functional completion; default measured render/metric loop unchanged from scenario harness 8bcb61651e9d6757eb7f1fe2aea29473bab80e4dfeefc5ac0135d6701e0abf2f";
            public string cohortCensusScope = "Unmeasured boundaries. Existing activeRendererComponents and explicit effectiveEnabledRenderers both require enabled, activeInHierarchy and !forceRenderingOff. Raw enabled and forced-off counts are separate. Baked active combined outputs are counted directly; reported active/suppressed/rejected/cohort values are owner counters. Missing production types/counters are -1.";
            public string shaderCensusScope = "Unique materials used by active enabled Renderer components plus native Terrain materials; supported/error/compiler checks at unmeasured boundaries. Does not prove every shader variant, CanvasRenderer material, or Android GLES compilation. Screenshots still require manual review.";
            public string fixture = "Natural drop actions; camera uses public touch look. Unmeasured driving setup observes existing motors and real authored world plans: if no eligible motor is nearby, it places only the player near a real plan and waits for normal streaming/physics. Supported side placement uses actual static ground/road queries. Entry, movement and exit use the public vehicle session. Combat stages up to six existing armed rivals. Source assets are never saved.";
            public string drivingSetupScope = "At most64 diagnostic samples during the unchanged45s setup deadline; no explicit rendering or sampled phase is active. Real plan reads and player placement do not instantiate cars, change vehicle plans, force ownership, advance physics or assign velocity. Failure remains NO_SAFE_EXISTING_VEHICLE_ENTERED.";
            public int initialDrivingMotorCount = -1, initialDrivingSafeMotorCount = -1, drivingPlanApproaches;
            public List<DrivingSetupSample> drivingSetup = new List<DrivingSetupSample>(48);
            public bool editorOnly = true, softwareRenderer, actualAndroid60FpsVerified;
            public bool sawPlane, sawFreefall, sawOpening, sawGliding, sawLanding, landingControlsRestored, landingHasSupport;
            public bool vehicleEntered, vehicleExited, vehicleMoved, firearmAmmoConsumed, incomingCombatDamage;
            public int runtimeErrors, shaderErrorLogs, width, height, requestedMinimumRoster = 94;
            public int renderWidth = MeasuredRenderWidth, renderHeight = MeasuredRenderHeight;
            public int requestedMinimumIntervalSamples = MinimumSamples;
            public string renderPipeline;
            public string rendererDataName = "unavailable", rendererDataPath = "unavailable",
                rendererDepthPrimingName = "unavailable";
            public int rendererDataIndex = -1, rendererDepthPrimingMode = -1, cameraRendererOverride = -1;
            public bool urpHDR, cameraHDR, srpBatcher;
            public float urpRenderScale, urpShadowDistance, qualityShadowDistance;
            public int urpMSAA, urpShadowCascades, urpMainShadowmapResolution, qualityTextureMipmapLimit;
            public int peakSpawnedRivals, combatAliveAtStart, stagedArmedCombatants;
            public string firstErrorKind = "", compilerCertification = "Requires separate exact-commit Unity import log; this harness does not suppress compiler errors.";
            public double sceneLoadMilliseconds, driveDistanceMetres, drivePeakSpeedMetresPerSecond;
            public PhaseReport[] phases;
        }

        [Serializable]
        public sealed class TraceManifest
        {
            public bool enabled = TraceEnabled;
            public string marker = "AARIS_DRAW_MARKER_";
            public string encoding = "begin = phaseOrdinal*100000 + sampleNumber*2; end = begin+1; sampleNumber starts at 1. Markers bracket only explicit measured Camera.Render, not screenshots.";
            public TracePhase[] phases;
        }
        [Serializable]
        public sealed class TracePhase
        {
            public string name;
            public int phaseOrdinal, renderSamples, firstBegin, lastEnd;
        }

        static AarisUltra60ScenarioBenchmark()
        {
            EditorApplication.update += Tick;
            Application.logMessageReceived += OnLog;
        }

        private static string ReadOutput()
        {
            string path = Environment.GetEnvironmentVariable("AARIS_ULTRA60_REPORT_DIR");
            if (string.IsNullOrEmpty(path)) return "/tmp/aaris-ultra60-benchmark";
            path = Path.GetFullPath(path);
            if (!path.StartsWith("/tmp/", StringComparison.Ordinal))
                throw new InvalidOperationException("BENCHMARK_OUTPUT_MUST_BE_UNDER_TMP");
            return path;
        }

        private static float ReadSeconds()
        {
            string raw = Environment.GetEnvironmentVariable("AARIS_ULTRA60_SAMPLE_SECONDS");
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
                float.IsNaN(value) || float.IsInfinity(value)) return 5f;
            return Mathf.Clamp(value, 2f, 60f);
        }

        private static int ReadMinimumSamples()
        {
            string value = Environment.GetEnvironmentVariable("AARIS_ULTRA60_MIN_SAMPLES");
            return int.TryParse(value, out int count) ? Mathf.Clamp(count, 3, 30) : 30;
        }

        [MenuItem("Aaris/CI/Capture Ultra60 complete scenario (Editor only)")]
        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("CI_BATCHMODE_ONLY");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("EDITOR_BUSY");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("UNSAVED_SCENE");
            Directory.CreateDirectory(Output);
            SessionState.SetString(Session, "load");
            SessionState.SetBool(Session + ".initialized", false);
            SessionState.SetInt(Session + ".errors", 0);
            SessionState.SetInt(Session + ".shaderErrors", 0);
            SessionState.SetString(Session + ".firstError", "");
            SessionState.SetFloat(Session + ".deadline", (float)EditorApplication.timeSinceStartup + 900f);
            File.WriteAllText(Path.Combine(Output, "status.txt"), "STARTING actual-map Editor benchmark\n");
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            if (string.IsNullOrEmpty(SessionState.GetString(Session, ""))) return;
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            SessionState.SetInt(Session + ".errors", SessionState.GetInt(Session + ".errors", 0) + 1);
            if (!string.IsNullOrEmpty(message) && message.IndexOf("shader", StringComparison.OrdinalIgnoreCase) >= 0)
                SessionState.SetInt(Session + ".shaderErrors", SessionState.GetInt(Session + ".shaderErrors", 0) + 1);
            if (string.IsNullOrEmpty(SessionState.GetString(Session + ".firstError", "")))
                SessionState.SetString(Session + ".firstError", type.ToString());
        }

        private static void Tick()
        {
            string state = SessionState.GetString(Session, "");
            if (string.IsNullOrEmpty(state)) return;
            if (state == "stop")
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                int code = SessionState.GetInt(Session + ".exit", 1);
                // OnDisable/OnDestroy errors occur after Finish requests exit
                // from Play Mode. Include them instead of certifying only the
                // pre-teardown part of the session. Domain reload can reset
                // static memory, so update the already persisted report.
                int finalErrors = SessionState.GetInt(Session + ".errors", 0);
                string finalPath = Path.Combine(Output, "report.json");
                if (finalErrors > 0 && File.Exists(finalPath))
                {
                    Report final = JsonUtility.FromJson<Report>(File.ReadAllText(finalPath));
                    final.runtimeErrors = finalErrors;
                    final.shaderErrorLogs = SessionState.GetInt(Session + ".shaderErrors", 0);
                    final.firstErrorKind = SessionState.GetString(Session + ".firstError", "");
                    if (final.status == "passed_editor_scenarios") final.reason = "PLAYMODE_TEARDOWN_ERRORS";
                    final.status = "failed_or_incomplete";
                    File.WriteAllText(finalPath, JsonUtility.ToJson(final, true));
                    code = 1;
                }
                SessionState.SetString(Session, "");
                EditorApplication.Exit(code);
                return;
            }
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Session + ".deadline", 0))
            { Finish(false, "SCENARIO_DEADLINE_EXCEEDED"); return; }
            try
            {
                if (state == "load")
                {
                    if (EditorApplication.isUpdating || EditorApplication.isCompiling) return;
                    WriteStatus("scene_opening " + ScenePath);
                    double began = EditorApplication.timeSinceStartup;
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                    SessionState.SetFloat(Session + ".loadMs", (float)((EditorApplication.timeSinceStartup - began) * 1000d));
                    WriteStatus("scene_opened " + ScenePath);
                    SessionState.SetString(Session, "play");
                    EditorApplication.isPlaying = true;
                    return;
                }
                if (!EditorApplication.isPlaying) return;
                if (Time.frameCount == lastFrame || Time.frameCount == 0) return;
                lastFrame = Time.frameCount;
                if (report == null)
                {
                    if (SessionState.GetBool(Session + ".initialized", false))
                    { Finish(false, "DOMAIN_RELOAD_DURING_CAPTURE"); return; }
                    Initialize();
                    return;
                }
                if (SessionState.GetInt(Session + ".errors", 0) >= 30)
                { Finish(false, "REPEATED_RUNTIME_ERRORS"); return; }
                ObserveDrop();
                if (current != null) SampleFrame();
                if (health != null && !health.IsDead && health.CurrentHealth < health.MaximumHealth * .75f)
                    health.RestoreHealth(health.MaximumHealth); // Fixture survival only; no fake damage.
                Advance();
            }
            catch (Exception ex)
            {
                Finish(false, "HARNESS_" + ex.GetType().Name +
                    (ex is InvalidOperationException && ex.Message.Length < 100 ? "_" + ex.Message : ""));
            }
        }

        private static void Initialize()
        {
            player = Object.FindFirstObjectByType<AarisThirdPersonPlayer>();
            director = Object.FindFirstObjectByType<AarisOfflineBRDirector>();
            drop = player != null ? player.GetComponent<AarisOfflineDrop>() : null;
            health = player != null ? player.GetComponent<AarisOfflineHealth>() : null;
            playerCapsule = player != null ? player.GetComponent<CharacterController>() : null;
            vehicleSession = player != null ? player.GetComponent<AarisVehicleSession>() : null;
            if (player == null || director == null || player.AimCamera == null ||
                drop == null || health == null || playerCapsule == null)
            {
                // Only runs before measurements exist. Make missing script
                // bindings observable without allocating in sampled frames.
                if (EditorApplication.timeSinceStartup >= nextInitializationDiagnostic)
                {
                    nextInitializationDiagnostic = EditorApplication.timeSinceStartup + 1d;
                    var missing = new List<string>(6);
                    if (player == null) missing.Add("AarisThirdPersonPlayer");
                    if (director == null) missing.Add("AarisOfflineBRDirector");
                    if (player == null || player.AimCamera == null) missing.Add("AimCamera");
                    if (drop == null) missing.Add("AarisOfflineDrop");
                    if (health == null) missing.Add("AarisOfflineHealth");
                    if (playerCapsule == null) missing.Add("CharacterController");
                    WriteStatus("waiting_initialization missing=" + string.Join(",", missing));
                }
                return;
            }
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("NULL_GRAPHICS_DEVICE");
            SessionState.SetBool(Session + ".initialized", true);
            report = NewReport();
            completed.Clear();
            histogram = new int[HistogramBins];
            rosterField = RequireField(typeof(AarisOfflineBRDirector), "rivals");
            spawnedField = RequireField(typeof(AarisOfflineBRDirector), "spawnedRivals");
            aliveField = RequireField(typeof(AarisOfflineBRDirector), "activeRivals");
            spawnCompleteField = RequireField(typeof(AarisOfflineBRDirector), "spawnComplete");
            main = Start(ProfilerCategory.Internal, "Main Thread");
            draws = Start(ProfilerCategory.Render, "Draw Calls Count");
            setPass = Start(ProfilerCategory.Render, "SetPass Calls Count");
            gc = Start(ProfilerCategory.Memory, "GC Allocated In Frame");
            if (TraceEnabled)
            {
                traceEvent = GetAarisTraceMarker();
                if (traceEvent == IntPtr.Zero) throw new InvalidOperationException("NATIVE_TRACE_CALLBACK_UNAVAILABLE");
            }
            measuredTarget = new RenderTexture(MeasuredRenderWidth, MeasuredRenderHeight, 24, RenderTextureFormat.ARGB32);
            measuredTarget.Create();
            health.Damaged += OnDamage;
            stage = 1;
            BeginPhase("plane");
        }

        private static Report NewReport()
        {
            string gpu = SystemInfo.graphicsDeviceName ?? "";
            return new Report {
                unityVersion = Application.unityVersion, graphicsAPI = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDevice = gpu, softwareRenderer = gpu.IndexOf("llvmpipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    gpu.IndexOf("softpipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    gpu.IndexOf("Software", StringComparison.OrdinalIgnoreCase) >= 0,
                width = Screen.width, height = Screen.height,
                sceneLoadMilliseconds = SessionState.GetFloat(Session + ".loadMs", -1f)
            };
        }

        private static void ObserveDrop()
        {
            if (drop == null) return;
            report.sawPlane |= drop.Phase == AarisOfflineDrop.DropPhase.InPlane;
            report.sawFreefall |= drop.Phase == AarisOfflineDrop.DropPhase.Freefall;
            report.sawOpening |= drop.Phase == AarisOfflineDrop.DropPhase.Opening;
            report.sawGliding |= drop.Phase == AarisOfflineDrop.DropPhase.Gliding;
            report.sawLanding |= drop.HasLanded;
        }

        private static void Advance()
        {
            float elapsed = Time.unscaledTime - phaseBegan;
            if (stage == 1 && Ready(SampleSeconds))
            {
                if (drop.Phase != AarisOfflineDrop.DropPhase.InPlane)
                    throw new InvalidOperationException("PLANE_ENDED_BEFORE_SAMPLE");
                EndPhase(true, "NATURAL_AIRCRAFT");
                drop.OnAction();
                if (!drop.IsFreefall) throw new InvalidOperationException("EJECT_DID_NOT_ENTER_FREEFALL");
                stage = 2; BeginPhase("freefall");
            }
            else if (stage == 2 && elapsed >= 1.1f && current.frames >= 2)
            {
                EndPhase(drop.IsFreefall, "REAL_EJECT_ACTION");
                drop.OnAction();
                stage = 3; BeginPhase("canopy_to_landing");
            }
            else if (stage == 3 && drop.HasLanded)
            {
                EndPhase(report.sawOpening && report.sawGliding, "NATURAL_DESCENT");
                report.landingControlsRestored = player.enabled && playerCapsule.enabled;
                report.landingHasSupport = HasSupport(player.transform.position);
                stage = 4; BeginPhase("landed_streaming");
            }
            else if (stage == 4 && Ready(SampleSeconds))
            {
                EndPhase(report.landingControlsRestored && report.landingHasSupport, "FOOT_CONTROL_AND_SUPPORT_CHECK");
                stage = 5; BeginPhase("fast_camera_rotation");
            }
            else if (stage == 5)
            {
                // Same public look authority as the mobile fire-drag controls.
                // Alternate large sweeps; no reflection or allocation per frame.
                player.ForwardLookDelta(new Vector2(((Time.frameCount / 30) & 1) == 0 ? 90f : -90f, 0f));
                if (!Ready(SampleSeconds)) return;
                EndPhase(true, "PUBLIC_TOUCH_LOOK_SWEEPS");
                stage = 6; stageBegan = Time.unscaledTime; nextSetupAt = 0f; driveAttempt = 0;
            }
            else if (stage == 6) PrepareDriving();
            else if (stage == 7)
            {
                if (vehicleSession == null || !vehicleSession.IsDriving || selectedCar == null)
                    throw new InvalidOperationException("VEHICLE_OWNERSHIP_LOST");
                vehicleSession.SetSteering(1f, 0f);
                drivePeakSpeed = Mathf.Max(drivePeakSpeed, selectedCar.SpeedMetresPerSecond);
                if (!ReadyForFunctionalCompletion(SampleSeconds)) return;
                report.driveDistanceMetres = Vector3.Distance(driveOrigin, selectedCar.transform.position);
                report.drivePeakSpeedMetresPerSecond = drivePeakSpeed;
                report.vehicleMoved = report.driveDistanceMetres >= 3d && drivePeakSpeed >= 1f;
                EndPhase(report.vehicleMoved, "REAL_VEHICLE_INPUT_AND_PHYSICS");
                vehicleSession.SetSteering(0f, 0f);
                stage = 8; stageBegan = Time.unscaledTime;
            }
            else if (stage == 8)
            {
                if (vehicleSession.IsDriving)
                {
                    // Reverse is the real service brake while travelling forward.
                    vehicleSession.SetSteering(selectedCar.SpeedMetresPerSecond > .25f ? -1f : 0f, 0f);
                    if (selectedCar.IsSafeToExit) vehicleSession.TryEnterOrExit();
                }
                if (!vehicleSession.IsDriving)
                {
                    report.vehicleExited = true;
                    stage = 9; stageBegan = Time.unscaledTime; nextSetupAt = 0f;
                }
                else if (Time.unscaledTime - stageBegan > 25f)
                    throw new InvalidOperationException("VEHICLE_SAFE_EXIT_TIMEOUT");
            }
            else if (stage == 9) PrepareCombat();
            else if (stage == 10 && ReadyForFunctionalCompletion(Mathf.Max(10f, SampleSeconds)))
            {
                int spent = 0;
                for (int i = 0; i < combatants.Length; i++)
                    if (combatants[i] != null && (int)Read(combatants[i], "carriedMagazine") < combatAmmoStart[i]) spent++;
                report.firearmAmmoConsumed = spent > 0;
                report.incomingCombatDamage = damageEvents > combatDamageStart;
                bool combatPass = report.peakSpawnedRivals >= 94 && report.stagedArmedCombatants > 0 &&
                    report.firearmAmmoConsumed && report.incomingCombatDamage;
                EndPhase(combatPass, "REAL_ROSTER_EXISTING_ARMED_ACTORS_NO_SYNTHETIC_DAMAGE");
                bool pass = report.sawPlane && report.sawFreefall && report.sawOpening && report.sawGliding &&
                    report.sawLanding && report.landingControlsRestored && report.landingHasSupport &&
                    report.vehicleEntered && report.vehicleExited && report.vehicleMoved && combatPass;
                for (int i = 0; i < completed.Count; i++) pass &= completed[i].status == "passed";
                Finish(pass, pass ? "SCENARIOS_COMPLETED_EDITOR_ONLY" : "SCENARIO_ASSERTION_FAILED");
            }
        }

        private static void PrepareDriving()
        {
            if (current != null) throw new InvalidOperationException("DRIVING_SETUP_INSIDE_MEASURED_PHASE");
            if (Time.unscaledTime - stageBegan > 45f)
            {
                completed.Add(new PhaseReport { name = "driving", status = "failed", reason = "NO_SAFE_EXISTING_VEHICLE_ENTERED" });
                stage = 9; stageBegan = Time.unscaledTime; nextSetupAt = 0f;
                SaveReport();
                return;
            }
            if (Time.unscaledTime < nextSetupAt) return;
            nextSetupAt = Time.unscaledTime + 1f;
            if (vehicleSession == null) vehicleSession = player.GetComponent<AarisVehicleSession>();
            if (drivingWorld == null) drivingWorld = Object.FindFirstObjectByType<AarisVehicleWorld>();
            if (drivingNearbyColliders == null) drivingNearbyColliders = Object.FindFirstObjectByType<AarisNearbyColliders>();
            if (drivingNearbyColliders != null && drivingInactiveObstacleQuery == null)
            {
                drivingInactiveObstacleQuery = typeof(AarisNearbyColliders).GetMethod("HasPotentialInactiveObstacle", Fields,
                    null, new[] { typeof(Vector3), typeof(Vector3), typeof(Quaternion), typeof(bool) }, null);
                if (drivingInactiveObstacleQuery == null || drivingInactiveObstacleQuery.ReturnType != typeof(bool))
                    throw new InvalidOperationException("DRIVING_INACTIVE_COLLIDER_QUERY_UNAVAILABLE");
            }
            AarisVehicleMotor[] cars = Object.FindObjectsByType<AarisVehicleMotor>(FindObjectsSortMode.None);
            int safeCount = 0, enabledCount = 0;
            foreach (AarisVehicleMotor candidate in cars)
                if (candidate.isActiveAndEnabled)
                { enabledCount++; if (!candidate.IsOccupied && candidate.IsSafeToExit) safeCount++; }
            if (report.initialDrivingMotorCount < 0)
            {
                report.initialDrivingMotorCount = cars.Length;
                report.initialDrivingSafeMotorCount = safeCount;
                approachedDrivingPlans.Clear(); exhaustedDrivingCars.Clear();
                nextDrivingPlanApproach = 0f;
            }
            DrivingSetupSample sample = DrivingSetupSnapshot(cars.Length, enabledCount, safeCount);
            if (vehicleSession == null || !vehicleSession.isActiveAndEnabled || !player.isActiveAndEnabled)
            { RecordDrivingSetup(sample, "WAITING_FOR_EXISTING_WALK_AND_SESSION_AUTHORITY"); return; }
            if (selectedCar != null && vehicleSession.CanEnter)
            {
                sample.entryAttempted = true;
                sample.entrySucceeded = vehicleSession.TryEnterOrExit();
                if (sample.entrySucceeded)
                {
                    report.vehicleEntered = true;
                    selectedCar = vehicleSession.DrivenCar;
                    driveOrigin = selectedCar.transform.position; drivePeakSpeed = 0f;
                    RecordDrivingSetup(sample, "PUBLIC_ENTRY_SUCCEEDED");
                    stage = 7; BeginPhase("driving");
                    return;
                }
            }
            if (selectedCar != null && (!selectedCar.isActiveAndEnabled || selectedCar.IsOccupied || driveAttempt >= 8))
            {
                // Try the last supported side on this tick BEFORE retiring
                // that candidate; never reselect the same failed car forever.
                exhaustedDrivingCars.Add(selectedCar.GetInstanceID()); selectedCar = null; driveAttempt = 0;
                RecordDrivingSetup(sample, "CANDIDATE_UNAVAILABLE_OR_ALL_EIGHT_SIDES_TRIED");
                return;
            }
            if (selectedCar == null)
            {
                float best = float.MaxValue;
                for (int i = 0; i < cars.Length; i++)
                {
                    AarisVehicleMotor car = cars[i];
                    if (!car.isActiveAndEnabled || car.IsOccupied || !car.IsSafeToExit ||
                        exhaustedDrivingCars.Contains(car.GetInstanceID())) continue;
                    float distance = (car.transform.position - player.transform.position).sqrMagnitude;
                    if (distance < best) { best = distance; selectedCar = car; }
                }
                driveAttempt = 0;
                if (selectedCar == null)
                {
                    bool approached = TryApproachAuthoredVehiclePlan(sample);
                    RecordDrivingSetup(sample, approached ? "APPROACHED_REAL_PLAN_WAITING_FOR_NATURAL_SPAWN" :
                        "WAITING_FOR_SAFE_EXISTING_MOTOR");
                    return;
                }
                sample = DrivingSetupSnapshot(cars.Length, enabledCount, safeCount);
            }
            if (!selectedCar.IsSafeToExit)
            { RecordDrivingSetup(sample, "WAITING_FOR_SELECTED_CAR_TO_SETTLE"); return; }
            Vector3 forward = Vector3.ProjectOnPlane(selectedCar.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .5f) forward = Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            int sideIndex = driveAttempt++;
            Vector3 offset = sideIndex == 0 ? right * 2.8f : sideIndex == 1 ? -right * 2.8f :
                sideIndex == 2 ? right * 2.1f + forward : sideIndex == 3 ? -right * 2.1f + forward :
                sideIndex == 4 ? right * 2.1f - forward : sideIndex == 5 ? -right * 2.1f - forward :
                sideIndex == 6 ? forward * 3.4f : -forward * 3.4f;
            sample.sideAttempt = sideIndex;
            bool supported = TryDrivingSetupFootPosition(selectedCar.transform.position + offset,
                selectedCar.transform.position.y - .71f, selectedCar.transform, sample, out Vector3 standing);
            if (supported && (standing - selectedCar.transform.position).sqrMagnitude < 16f)
            {
                TeleportPlayer(standing);
                RecordDrivingSetup(sample, "SUPPORTED_SIDE_PLAYER_PLACEMENT");
            }
            else RecordDrivingSetup(sample, "SIDE_REJECTED_SUPPORT_CLEARANCE_OR_ENTRY_DISTANCE");
        }

        private static DrivingSetupSample DrivingSetupSnapshot(int discovered, int enabled, int safe)
        {
            var sample = new DrivingSetupSample { discoveredMotorComponents = discovered, enabledMotors = enabled, safeMotors = safe,
                elapsedSeconds = Time.unscaledTime - stageBegan, timeScale = Time.timeScale,
                playerPosition = player.transform.position, footActive = player.isActiveAndEnabled,
                capsuleEnabled = playerCapsule.enabled, sessionActive = vehicleSession != null && vehicleSession.isActiveAndEnabled,
                canEnter = vehicleSession != null && vehicleSession.CanEnter };
            if (vehicleSession != null)
            {
                sample.sessionConfigured = Read(vehicleSession, "world") != null &&
                    Read(vehicleSession, "footMotor") != null && Read(vehicleSession, "followCamera") != null;
                var nearby = Read(vehicleSession, "nearbyCar") as AarisVehicleMotor;
                if (nearby != null) sample.nearbyVehicle = nearby.VehicleId;
            }
            if (selectedCar != null)
            {
                sample.selectedVehicle = selectedCar.VehicleId; sample.carPosition = selectedCar.transform.position;
                sample.distanceMetres = Vector3.Distance(sample.playerPosition, sample.carPosition);
                sample.selectedActive = selectedCar.isActiveAndEnabled; sample.selectedSafe = selectedCar.IsSafeToExit;
                sample.selectedOccupied = selectedCar.IsOccupied; sample.speedMetresPerSecond = selectedCar.TotalSpeedMetresPerSecond;
                var body = selectedCar.GetComponent<Rigidbody>();
                if (body != null) { sample.bodyKinematic = body.isKinematic; sample.angularSpeed = body.angularVelocity.magnitude; }
                var wheels = Read(selectedCar, "wheels") as WheelCollider[];
                if (wheels != null) foreach (WheelCollider wheel in wheels)
                    if (wheel != null && wheel.isGrounded) sample.groundedWheels++;
            }
            return sample;
        }

        private static void RecordDrivingSetup(DrivingSetupSample sample, string action)
        {
            sample.action = action;
            if (report.drivingSetup.Count < 64) report.drivingSetup.Add(sample);
            SaveReport(); // Unmeasured setup only; retains partial/failing evidence.
        }

        private static bool TryApproachAuthoredVehiclePlan(DrivingSetupSample sample)
        {
            if (drivingWorld == null || Time.unscaledTime < nextDrivingPlanApproach) return false;
            nextDrivingPlanApproach = Time.unscaledTime + 8f;
            var plans = Read(drivingWorld, "plans") as System.Collections.IEnumerable;
            if (plans == null) return false;
            object chosen = null;
            float best = float.MaxValue;
            foreach (object record in plans)
            {
                if (record == null || approachedDrivingPlans.Contains(record)) continue;
                var visible = Read(record, "visible") as AarisVehicleMotor;
                if (visible != null && visible.IsOccupied) continue;
                Vector3 position = (Vector3)Read(record, "position");
                float distance = (position - player.transform.position).sqrMagnitude;
                if (distance < best) { best = distance; chosen = record; }
            }
            if (chosen == null) return false;
            approachedDrivingPlans.Add(chosen);
            object point = Read(chosen, "point");
            sample.planId = point != null ? (string)Read(point, "id") : "unknown";
            Vector3 origin = (Vector3)Read(chosen, "position");
            Quaternion rotation = (Quaternion)Read(chosen, "rotation");
            float expectedFloor = origin.y - ((bool)Read(chosen, "previouslySpawned") ? .71f : 0f);
            // Keep the player outside the real car's spawn footprint. The
            // ordinary105m world streamer alone creates/settles that car.
            for (int side = 0; side < 4; side++)
            {
                Vector3 offset = rotation * (side == 0 ? Vector3.forward * 8f : side == 1 ? Vector3.back * 8f :
                    side == 2 ? Vector3.right * 8f : Vector3.left * 8f);
                if (!TryDrivingSetupFootPosition(origin + offset, expectedFloor, null, sample, out Vector3 position)) continue;
                TeleportPlayer(position);
                report.drivingPlanApproaches++;
                return true;
            }
            return false;
        }

        private static bool TryDrivingSetupFootPosition(Vector3 option, float expectedFloor,
            Transform vehicle, DrivingSetupSample sample, out Vector3 position)
        {
            position = default;
            sample.supportFound = sample.placementClear = false;
            sample.supportCollider = ""; sample.setupPosition = Vector3.zero;
            if (Mathf.Abs(option.x) > 973f || Mathf.Abs(option.z) > 973f) return false;
            Ray ray = new Ray(new Vector3(option.x, expectedFloor + 3f, option.z), Vector3.down);
            int hits = Physics.RaycastNonAlloc(ray, groundHits, 6f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits >= groundHits.Length) return false; // A partial query cannot prove support.
            float nearest = float.PositiveInfinity;
            Collider floor = null; Vector3 support = Vector3.zero;
            for (int i = 0; i < hits; i++)
            {
                RaycastHit hit = groundHits[i]; Collider receiver = hit.collider;
                if (receiver == null || receiver is CharacterController || receiver.attachedRigidbody != null || receiver.gameObject.layer == 4 ||
                    receiver.transform.IsChildOf(player.transform) ||
                    (vehicle != null && receiver.transform.IsChildOf(vehicle)) ||
                    hit.normal.y < .707f || hit.point.y < -38.07552f + .35f ||
                    Mathf.Abs(hit.point.y - expectedFloor) > 1.5f || hit.distance >= nearest) continue;
                nearest = hit.distance; support = hit.point; floor = receiver;
            }
            sample.supportFound = floor != null;
            if (floor == null) return false;
            sample.supportCollider = floor.name;
            float radius = Mathf.Max(.12f, playerCapsule.radius - playerCapsule.skinWidth);
            Vector3 bottom = support + Vector3.up * (radius + .1f);
            Vector3 top = support + Vector3.up * (playerCapsule.height - radius + .1f);
            Vector3 capsuleCenter = (bottom + top) * .5f;
            Vector3 capsuleHalf = new Vector3(radius, Mathf.Abs(top.y - bottom.y) * .5f + radius, radius);
            // The existing runtime query is internal to Assembly-CSharp.
            // Read it through reflection only in this unmeasured Editor setup;
            // do not widen the game's API or wake/move distant colliders.
            if (drivingNearbyColliders == null || drivingInactiveObstacleQuery == null) return false;
            object blocked = drivingInactiveObstacleQuery.Invoke(drivingNearbyColliders,
                new object[] { capsuleCenter, capsuleHalf, Quaternion.identity, false });
            if (!(blocked is bool) || (bool)blocked) return false;
            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, drivingPlacementOverlaps,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (count >= drivingPlacementOverlaps.Length) return false;
            for (int i = 0; i < count; i++)
            {
                Collider obstacle = drivingPlacementOverlaps[i];
                if (obstacle != null && obstacle != floor && !obstacle.transform.IsChildOf(player.transform)) return false;
            }
            sample.placementClear = true;
            position = support + Vector3.up * .08f;
            sample.setupPosition = position;
            return true;
        }

        private static void PrepareCombat()
        {
            if (Time.unscaledTime < nextSetupAt) return;
            nextSetupAt = Time.unscaledTime + 1f;
            int spawned = (int)spawnedField.GetValue(director);
            report.peakSpawnedRivals = Math.Max(report.peakSpawnedRivals, spawned);
            if (!(bool)spawnCompleteField.GetValue(director) || spawned < 94)
            {
                if (Time.unscaledTime - stageBegan > 90f)
                    throw new InvalidOperationException("REAL_ROSTER_BELOW_94");
                return;
            }
            Array roster = (Array)rosterField.GetValue(director);
            report.combatAliveAtStart = (int)aliveField.GetValue(director);
            int count = 0;
            for (int i = 0; i < roster.Length && count < combatants.Length; i++)
            {
                Component bot = roster.GetValue(i) as Component;
                if (bot == null || Read(bot, "carriedGun") == null) continue;
                AarisOfflineHealth hp = bot.GetComponent<AarisOfflineHealth>();
                if (hp == null || hp.IsDead) continue;
                float angle = count * Mathf.PI / 3f;
                Vector3 destination = OnTerrain(player.transform.position +
                    new Vector3(Mathf.Sin(angle) * 14f, 0f, Mathf.Cos(angle) * 14f));
                CharacterController capsule = bot.GetComponent<CharacterController>();
                bool wasEnabled = capsule != null && capsule.enabled;
                if (wasEnabled) capsule.enabled = false;
                bot.transform.position = destination;
                Vector3 toward = player.transform.position - destination; toward.y = 0f;
                if (toward.sqrMagnitude > .001f) bot.transform.rotation = Quaternion.LookRotation(toward);
                if (wasEnabled) capsule.enabled = true;
                combatants[count] = bot;
                combatAmmoStart[count] = (int)Read(bot, "carriedMagazine");
                count++;
            }
            if (count == 0)
            {
                if (Time.unscaledTime - stageBegan > 90f)
                    throw new InvalidOperationException("NO_EXISTING_ARMED_RIVAL");
                return;
            }
            report.stagedArmedCombatants = count;
            Physics.SyncTransforms();
            combatDamageStart = damageEvents;
            stage = 10; BeginPhase("full_roster_local_gunfight");
        }

        private static void TeleportPlayer(Vector3 position)
        {
            bool enabled = playerCapsule.enabled;
            if (enabled) playerCapsule.enabled = false;
            player.transform.position = position;
            if (enabled) playerCapsule.enabled = true;
            player.CancelAutoSprint();
            Physics.SyncTransforms();
        }

        private static Vector3 OnTerrain(Vector3 point)
        {
            Terrain[] terrains = Terrain.activeTerrains;
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain t = terrains[i];
                if (t == null || t.terrainData == null) continue;
                Vector3 origin = t.transform.position, size = t.terrainData.size;
                if (point.x < origin.x || point.z < origin.z || point.x > origin.x + size.x || point.z > origin.z + size.z) continue;
                point.y = t.SampleHeight(point) + origin.y + .25f;
                return point;
            }
            throw new InvalidOperationException("FIXTURE_POINT_OUTSIDE_NATIVE_TERRAIN");
        }

        private static bool HasSupport(Vector3 point)
        {
            int count = Physics.RaycastNonAlloc(point + Vector3.up * .3f, Vector3.down,
                groundHits, 2f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (count >= groundHits.Length) return false;
            for (int i = 0; i < count; i++)
                if (groundHits[i].collider != null && !groundHits[i].collider.transform.IsChildOf(player.transform) &&
                    groundHits[i].normal.y > .35f) return true;
            return false;
        }

        private static void OnDamage(AarisOfflineHealth receiver, float amount, GameObject attacker)
        { if (amount > 0f && attacker != null) damageEvents++; }

        private static bool Ready(float seconds)
        { return current != null && current.sampledSeconds >= seconds && current.frames >= MinimumSamples; }

        private static bool ReadyForFunctionalCompletion(float seconds)
        {
            if (!Ready(seconds)) return false;
            if (!FunctionalTransit) return true;
            // Slow software renders advance unscaled wall time much faster
            // than Unity's clamped game clock. Give the unchanged real vehicle
            // physics and rival gameplay their requested natural duration.
            if (stage == 7) return Time.fixedTimeAsDouble - phaseFixedBegan >= seconds;
            if (stage == 10) return Time.timeAsDouble - phaseGameBegan >= seconds;
            return true;
        }

        private static void BeginPhase(string name)
        {
            current = new PhaseReport { name = name, tracePhaseOrdinal = completed.Count + 1 };
            phaseGameBegan = Time.timeAsDouble; phaseFixedBegan = Time.fixedTimeAsDouble;
            Array.Clear(histogram, 0, histogram.Length);
            WriteStatus(stage == 1 ? "initialized_before_census " + name : "census_begin " + name);
            current.startCensus = TakeCensus();
            CaptureScreenshot(current);
            phaseBegan = Time.unscaledTime;
            // Airborne states can be shorter than eight software-rendered
            // frames. Exclude the capture frame, then observe real frames;
            // do not hold or fake the game's drop phase to fill a benchmark.
            warmup = stage == 2 || stage == 3 ? 1 : WarmFrames;
            previousFrameRendered = false;
            WriteStatus(name);
        }

        private static void SampleFrame()
        {
            if (FunctionalTransit && stage == 3 && current.explicitRenderSamples >= 4)
            {
                // Opt-in functional coverage on slow software hosts. The real
                // drop Update/physics/Advance still run naturally; only this
                // explicit camera workload stops after its four-render window.
                // Do not bridge a performance interval across unrendered ticks.
                previousFrameRendered = false;
                report.skippedTransitFrames++;
                current.skippedExplicitRenderFrames++;
                return;
            }
            if (FunctionalTransit && (stage == 7 || stage == 10) &&
                Ready(stage == 7 ? SampleSeconds : Mathf.Max(10f, SampleSeconds)))
            {
                // Retain the first complete measured window unchanged, then
                // run real held input/physics/AI without more software renders.
                // Advance still runs every real frame and checks its own game
                // or fixed clock; never bridge a sample across skipped ticks.
                previousFrameRendered = false;
                current.skippedExplicitRenderFrames++;
                report.skippedFunctionalCompletionFrames++;
                return;
            }
            if (warmup-- > 0) { current.warmupFramesExcluded++; return; }
            // Batch Play Mode does not guarantee a Game-view render. Make the
            // rendering workload explicit, with a reused target and no readback
            // or screenshot encoding in the measured sample path.
            Camera camera = player.AimCamera;
            if (camera == null || measuredTarget == null)
                throw new InvalidOperationException("MEASURED_CAMERA_UNAVAILABLE");
            RenderTexture priorTarget = camera.targetTexture, priorActive = RenderTexture.active;
            int traceBegin = current.tracePhaseOrdinal * 100000 + (current.explicitRenderSamples + 1) * 2;
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (TraceEnabled) GL.IssuePluginEvent(traceEvent, traceBegin);
            try { camera.targetTexture = measuredTarget; camera.Render(); }
            finally
            {
                camera.targetTexture = priorTarget; RenderTexture.active = priorActive;
                if (TraceEnabled) GL.IssuePluginEvent(traceEvent, traceBegin + 1);
            }
            double renderMs = (System.Diagnostics.Stopwatch.GetTimestamp() - began) *
                (1000d / System.Diagnostics.Stopwatch.Frequency);
            current.explicitRenderSamples++;
            current.synchronousCameraRenderMilliseconds.Add(renderMs);
            bool completedRenderedInterval = previousFrameRendered && previousRenderedFrame == Time.frameCount - 1;
            previousFrameRendered = true; previousRenderedFrame = Time.frameCount;
            if (!completedRenderedInterval) return;
            double ms = Time.unscaledDeltaTime * 1000d;
            if (!(ms > 0d) || double.IsInfinity(ms) || double.IsNaN(ms)) return;
            int bucket = (int)Math.Min(HistogramBins - 1, Math.Floor(ms * 4d));
            histogram[bucket]++;
            current.frames++; current.sampledSeconds += ms / 1000d;
            current.worstMs = Math.Max(current.worstMs, ms);
            if (ms > 1000d / 60d) current.over16_67ms++;
            if (ms > 1000d / 30d) current.over33_33ms++;
            if (ms > 50d) current.over50ms++;
            if (ms > 100d) current.over100ms++;
            current.mainThreadNanoseconds.Add(ReadCounter(main, ProfilerMarkerDataUnit.TimeNanoseconds));
            current.drawCalls.Add(ReadCounter(draws, ProfilerMarkerDataUnit.Count));
            current.setPassCalls.Add(ReadCounter(setPass, ProfilerMarkerDataUnit.Count));
            current.gcBytes.Add(ReadCounter(gc, ProfilerMarkerDataUnit.Bytes));
        }

        private static void EndPhase(bool pass, string reason)
        {
            if (current == null) return;
            current.status = pass ? "passed" : "failed"; current.reason = reason;
            current.gameElapsedSeconds = Math.Max(0d, Time.timeAsDouble - phaseGameBegan);
            current.fixedElapsedSeconds = Math.Max(0d, Time.fixedTimeAsDouble - phaseFixedBegan);
            if (current.frames > 0)
            {
                current.fps = current.frames / current.sampledSeconds;
                current.p50ms = Percentile(.50d); current.p95ms = Percentile(.95d); current.p99ms = Percentile(.99d);
            }
            else { current.status = "failed"; current.reason += "_NO_MEASURED_FRAMES"; }
            InvalidateEmptyRenderCounter(current.drawCalls, current.explicitRenderSamples);
            InvalidateEmptyRenderCounter(current.setPassCalls, current.explicitRenderSamples);
            current.mainThreadNanoseconds.Finish(); current.drawCalls.Finish();
            current.setPassCalls.Finish(); current.gcBytes.Finish();
            current.synchronousCameraRenderMilliseconds.Finish();
            current.endCensus = TakeCensus();
            completed.Add(current); current = null;
            SaveReport();
        }

        private static void InvalidateEmptyRenderCounter(Metric metric, int renderedSamples)
        {
            if (renderedSamples <= 0 || metric.samples <= 0 || metric.maximum != 0d) return;
            metric.unavailableFrames += metric.samples;
            metric.samples = 0; metric.sum = 0d;
            metric.mean = metric.minimum = metric.maximum = -1d;
            metric.unavailableReason = "ALL_ZERO_DESPITE_EXPLICIT_CAMERA_RENDER_USE_NATIVE_GL_TRACE";
        }

        private static double Percentile(double q)
        {
            int rank = (int)Math.Ceiling(current.frames * q), count = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                count += histogram[i];
                if (count < rank) continue;
                if (i == histogram.Length - 1) { current.percentileOverflow = true; return 4000d; }
                return (i + 1) * .25d; // Upper edge of the 0.25-ms bin.
            }
            return -1d;
        }

        private static Census TakeCensus()
        {
            Terrain[] nativeTerrains = Terrain.activeTerrains;
            var census = new Census { loadedScenes = SceneManager.sceneCount, activeTerrains = nativeTerrains.Length,
                terrains = new TerrainMetadata[nativeTerrains.Length] };
            var materials = new HashSet<Material>();
            var shaders = new HashSet<Shader>();
            var issues = new List<string>(16);
            for (int i = 0; i < nativeTerrains.Length; i++)
            {
                Terrain terrain = nativeTerrains[i]; TerrainData data = terrain.terrainData;
                Material material = terrain.materialTemplate;
                if (material != null) materials.Add(material);
                census.terrains[i] = new TerrainMetadata {
                    name = terrain.name, materialShader = material != null && material.shader != null ? material.shader.name : "unavailable",
                    shadowCastingMode = terrain.shadowCastingMode.ToString(),
                    terrainLayers = data != null ? data.terrainLayers.Length : -1,
                    alphamapResolution = data != null ? data.alphamapResolution : -1,
                    alphamapTextures = data != null ? data.alphamapTextureCount : -1,
                    detailWidth = data != null ? data.detailWidth : -1, detailHeight = data != null ? data.detailHeight : -1,
                    treeInstances = data != null ? data.treeInstanceCount : -1,
                    treePrototypes = data != null ? data.treePrototypes.Length : -1,
                    drawHeightmap = terrain.drawHeightmap, drawTreesAndFoliage = terrain.drawTreesAndFoliage,
                    drawInstanced = terrain.drawInstanced, basemapDistance = terrain.basemapDistance,
                    detailDistance = terrain.detailObjectDistance, treeDistance = terrain.treeDistance,
                    treeBillboardDistance = terrain.treeBillboardDistance,
                    heightmapPixelError = terrain.heightmapPixelError
                };
            }
            Renderer[] renderers = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].enabled && renderers[i].gameObject.activeInHierarchy)
                { census.enabledRendererComponents++; if (renderers[i].forceRenderingOff) census.forceSuppressedRenderers++; }
                if (renderers[i].enabled && !renderers[i].forceRenderingOff && renderers[i].gameObject.activeInHierarchy)
                {
                    census.activeRendererComponents++;
                    if (renderers[i] is SkinnedMeshRenderer) census.activeSkinnedRenderers++;
                    Material[] slots = renderers[i].sharedMaterials;
                    for (int n = 0; n < slots.Length; n++)
                        if (slots[n] == null) census.nullMaterialSlots++;
                        else materials.Add(slots[n]);
                }
            }
            census.uniqueActiveMaterials = materials.Count;
            foreach (Material material in materials)
            {
                Shader shader = material.shader;
                if (shader == null)
                {
                    census.missingShaders++;
                    if (issues.Count < 32) issues.Add("missing_shader material=" + material.name);
                }
                else shaders.Add(shader);
            }
            census.uniqueActiveShaders = shaders.Count;
            foreach (Shader shader in shaders)
            {
                if (!shader.isSupported)
                {
                    census.unsupportedShaders++;
                    if (issues.Count < 32) issues.Add("unsupported_shader " + shader.name);
                }
                if (shader.name.IndexOf("InternalErrorShader", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    census.internalErrorShaders++;
                    if (issues.Count < 32) issues.Add("internal_error_shader " + shader.name);
                }
                if (ShaderUtil.ShaderHasError(shader))
                {
                    census.shadersWithCompilerErrors++;
                    if (issues.Count < 32) issues.Add("compiler_error_shader " + shader.name);
                }
            }
            census.activeShaderCensusPassed = census.missingShaders == 0 && census.unsupportedShaders == 0 &&
                census.internalErrorShaders == 0 && census.shadersWithCompilerErrors == 0;
            census.shaderIssues = issues.ToArray();
            GameObject[] objects = Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < objects.Length; i++)
                if (objects[i].scene.IsValid() && objects[i].scene.isLoaded)
                    census.missingBehaviourComponents += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(objects[i]);
            Animator[] animators = Object.FindObjectsByType<Animator>(FindObjectsSortMode.None);
            for (int i = 0; i < animators.Length; i++)
                if (animators[i].isActiveAndEnabled) census.activeAnimatorComponents++;
            if (director != null && spawnedField != null)
            {
                census.spawnedRivals = (int)spawnedField.GetValue(director);
                census.aliveRivals = (int)aliveField.GetValue(director);
                report.peakSpawnedRivals = Math.Max(report.peakSpawnedRivals, census.spawnedRivals);
            }
            // Baseline has no sector type. Reflection keeps this exact Editor
            // harness independent of the production changes being compared.
            Type sectorType = typeof(AarisOfflineBRDirector).Assembly.GetType("Aaris.MapReview.AarisWorldSectorRuntime");
            Object sectors = sectorType == null ? null : Object.FindFirstObjectByType(sectorType);
            if (sectors != null)
            {
                census.sectorStaticLods = ReadPublicCounter(sectors, "StaticLodCount");
                census.sectorMacroLods = ReadPublicCounter(sectors, "MacroLodCount");
                census.sectorBatchedRenderers = ReadPublicCounter(sectors, "BatchedRendererCount");
                census.sectorUnreadableRenderers = ReadPublicCounter(sectors, "UnreadableRendererCount");
            }
            census.effectiveEnabledRenderers = census.activeRendererComponents;
            ReadBakedBatchCounters(census);
            return census;
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

        private static int ReadPublicCounter(Object value, string name)
        {
            PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return property != null && property.PropertyType == typeof(int) ? (int)property.GetValue(value) : -1;
        }

        private static void CaptureScreenshot(PhaseReport phase)
        {
            Camera camera = player != null ? player.AimCamera : Camera.main;
            if (camera == null) { phase.screenshotStatus = "camera unavailable"; return; }
            WriteStatus("screenshot_begin " + phase.name);
            RenderTexture previous = camera.targetTexture, active = RenderTexture.active;
            var target = new RenderTexture(640, 360, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(640, 360, TextureFormat.RGB24, false);
            try
            {
                target.Create(); camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, 640, 360), 0, 0); texture.Apply(false, false);
                Color32[] pixels = texture.GetPixels32();
                double total = 0d, squared = 0d; int magenta = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 p = pixels[i]; double grey = (p.r + p.g + p.b) / 3d;
                    total += grey; squared += grey * grey;
                    if (p.r > 190 && p.b > 190 && p.g < 75) magenta++;
                }
                double average = total / pixels.Length;
                phase.screenshotPixelVariation = (float)Math.Sqrt(Math.Max(0d, squared / pixels.Length - average * average));
                phase.screenshotMagentaFraction = (float)magenta / pixels.Length;
                phase.screenshot = phase.name + ".png";
                phase.screenshotObservedDropPhase = drop == null ? "unavailable" : drop.Phase.ToString();
                File.WriteAllBytes(Path.Combine(Output, phase.screenshot), texture.EncodeToPNG());
                phase.screenshotStatus = phase.screenshotPixelVariation >= 3f && phase.screenshotMagentaFraction < .05f
                    ? "nonblank_render_manual_visual_review_required" : "blank_or_magenta_review_required";
            }
            finally
            {
                camera.targetTexture = previous; RenderTexture.active = active;
                target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(texture);
            }
            WriteStatus("screenshot_end " + phase.name);
        }

        private static ProfilerRecorder Start(ProfilerCategory category, string name)
        { try { return ProfilerRecorder.StartNew(category, name, 1); } catch (Exception) { return default; } }
        private static long ReadCounter(ProfilerRecorder recorder, ProfilerMarkerDataUnit unit)
        { return recorder.Valid && recorder.Count > 0 && recorder.UnitType == unit ? Math.Max(-1L, recorder.LastValue) : -1L; }
        private static FieldInfo RequireField(Type type, string name)
        { return type.GetField(name, Fields) ?? throw new InvalidOperationException("MISSING_FIELD_" + name); }
        private static object Read(object value, string name) => RequireField(value.GetType(), name).GetValue(value);
        private static void WriteStatus(string value)
        { File.WriteAllText(Path.Combine(Output, "status.txt"), value + "\n"); }

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

        private static void SaveReport()
        {
            if (report == null) report = NewReport();
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            report.renderPipeline = pipeline == null ? "built-in" : pipeline.name;
            report.qualityShadowDistance = QualitySettings.shadowDistance;
            report.qualityTextureMipmapLimit = QualitySettings.globalTextureMipmapLimit;
            report.cameraHDR = player != null && player.AimCamera != null && player.AimCamera.allowHDR;
            if (pipeline is UniversalRenderPipelineAsset urp)
            {
                report.urpHDR = urp.supportsHDR; report.srpBatcher = urp.useSRPBatcher;
                report.urpRenderScale = urp.renderScale; report.urpMSAA = urp.msaaSampleCount;
                report.urpShadowDistance = urp.shadowDistance; report.urpShadowCascades = urp.shadowCascadeCount;
                report.urpMainShadowmapResolution = urp.mainLightShadowmapResolution;
                ReadRendererDataMetadata(report, urp, player != null ? player.AimCamera : null);
            }
            report.runtimeErrors = SessionState.GetInt(Session + ".errors", 0);
            report.shaderErrorLogs = SessionState.GetInt(Session + ".shaderErrors", 0);
            report.firstErrorKind = SessionState.GetString(Session + ".firstError", "");
            report.phases = completed.ToArray();
            Directory.CreateDirectory(Output);
            File.WriteAllText(Path.Combine(Output, "report.json"), JsonUtility.ToJson(report, true));
            if (TraceEnabled)
            {
                var manifest = new TraceManifest { phases = new TracePhase[completed.Count] };
                for (int i = 0; i < completed.Count; i++)
                {
                    PhaseReport phase = completed[i];
                    manifest.phases[i] = new TracePhase { name = phase.name, phaseOrdinal = phase.tracePhaseOrdinal,
                        renderSamples = phase.explicitRenderSamples, firstBegin = phase.tracePhaseOrdinal * 100000 + 2,
                        lastEnd = phase.tracePhaseOrdinal * 100000 + phase.explicitRenderSamples * 2 + 1 };
                }
                File.WriteAllText(Path.Combine(Output, "trace-markers.json"), JsonUtility.ToJson(manifest, true));
            }
        }

        private static void Finish(bool passed, string reason)
        {
            if (stopping) return;
            stopping = true;
            try
            {
                if (report == null) report = NewReport();
                if (current != null) EndPhase(false, reason);
                passed &= SessionState.GetInt(Session + ".errors", 0) == 0;
                report.status = passed ? "passed_editor_scenarios" : "failed_or_incomplete";
                report.reason = reason;
                SaveReport(); WriteStatus(report.status + " " + reason);
            }
            finally
            {
                if (health != null) health.Damaged -= OnDamage;
                if (vehicleSession != null) vehicleSession.SetSteering(0f, 0f);
                if (main.Valid) main.Dispose(); if (draws.Valid) draws.Dispose();
                if (setPass.Valid) setPass.Dispose(); if (gc.Valid) gc.Dispose();
                if (measuredTarget != null) { measuredTarget.Release(); Object.DestroyImmediate(measuredTarget); measuredTarget = null; }
                SessionState.SetInt(Session + ".exit", passed ? 0 : 1);
                SessionState.SetString(Session, "stop");
                if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            }
        }
    }
}
#endif
