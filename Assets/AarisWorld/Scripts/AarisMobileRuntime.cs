using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;
#if UNITY_ANDROID && !UNITY_EDITOR
using System.Threading.Tasks;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#endif

namespace Aaris.MapReview
{
    // The APK reviews a fixed terrain. Editor assets stay readable and editable.
    [DisallowMultipleComponent, DefaultExecutionOrder(-300)]
    public sealed class AarisMobileRuntime : MonoBehaviour
    {
        [SerializeField] private Terrain[] terrains;
        [SerializeField] private bool releaseTerrainPaintCpuCopies = false;
        [SerializeField] private int targetFrameRate = 60;
        [Header("Android runtime performance (adaptive internal render scale)")]
        [SerializeField] private bool adaptiveAndroidResolution = true;
        [SerializeField, Range(0.65f, 1f)] private float minimumAndroidRenderScale = 0.84f;
        private readonly float[] frameTimes = new float[180];
        private readonly float[] sortedTimes = new float[180];
        private int frameIndex, frameCount;
        private float sampleAt, unloadAfter;
        private bool lowMemoryPending;
        private AsyncOperation unloading;
        private string logPath;
        private long releasedPaintBytes;
        private string lastError = "";
        private int auditedTiles, visibleHeightmaps, paintedTiles;
        private int alphamapCount, invalidTerrainMaterials;
        private bool terrainLitAvailable;
        // Restore water shadow properties when this scene runtime exits.
        private MeshRenderer oceanShadowRenderer;
        private UnityEngine.Rendering.ShadowCastingMode originalOceanCasting;
        private bool originalOceanReceiving, changedOceanCasting, changedOceanReceiving;
        private const float ReportInterval = 15f;
#if UNITY_ANDROID && !UNITY_EDITOR
        private AarisOfflineDrop startupDrop;
        private float startupTime;
        private int startupSampleIndex;
        private Task<string> pendingLogWrite;
        // Limit main-thread async asset integration on budget Android GPUs.
        private UnityEngine.ThreadPriority originalBackgroundLoadPriority;
        private bool modifiedBackgroundLoadPriority;
        private float nextLongFrameReportAt;
        // Begin at native sharpness and adapt only after sustained FPS misses.
        private UniversalRenderPipelineAsset androidPipeline;
        private float originalRenderScale = 1f;
        private float lastAppliedRenderScale = 1f;
        private int originalMsaaSamples, appliedMsaaSamples;
        private bool modifiedMsaaSamples;
        private float originalMipBudgetMB, appliedMipBudgetMB;
        private bool modifiedMipBudget;
        private float originalShadowDistance, appliedShadowDistance;
        private bool modifiedShadowDistance;
        // Info log stack traces cost managed/native stack walking when gameplay
        // emits frequent telemetry. Retain messages and all warning/error traces.
        private StackTraceLogType previousInfoLogStackTrace;
        private bool infoLogStackTraceOverridden;
        private int originalShadowCascades, appliedShadowCascades;
        private bool modifiedShadowCascades;
        // Android-only, reversible controls for 2 GB phones. These never alter
        // serialized TerrainData, scenes, SpeedTree prefabs or material assets.
        private Terrain[] mobileFoliageTiles;
        private float[] originalDetailDistances, appliedDetailDistances;
        private float[] originalBillboardDistances, appliedBillboardDistances;
        private float[] originalTreeDistances, appliedTreeDistances;
        private int[] originalFullLodTreeCounts;
        private const int LowMemoryFullLodTreeLimit = 50;
        private TerrainData[] mobileWindData;
        private float[] originalGrassWindStrength;
        private WindZone[] mobileWindZones;
        private float[] originalWindMain, originalWindTurbulence;
        private float[] originalWindPulseMagnitude, originalWindPulseFrequency;
        private int suppressedWindZones, suppressedGrassTiles;
        private int slowIntervals, steadyIntervals, lastTargetFps;
        private float lastEffectiveTargetFps;
        // A CPU-bound stutter must not be "fixed" by blurring the GPU output.
        // This scratch array is reused; sampling runs at most once per 15 frames.
        private readonly FrameTiming[] mobileFrameTimings = new FrameTiming[1];
        private int timingFrameCounter, timingSamples, gpuLimitedSamples, cpuLimitedSamples;
        private double gpuFrameTimeTotalMs, cpuMainTimeTotalMs, cpuRenderTimeTotalMs;
#endif
        public static string LatestReport { get; private set; } = "Starting map...";

        private void Awake()
        {
            Application.targetFrameRate = targetFrameRate;
#if UNITY_ANDROID && !UNITY_EDITOR
            startupTime = Time.unscaledTime;
            originalBackgroundLoadPriority = Application.backgroundLoadingPriority;
            if (IsBudgetAndroidGpu() &&
                originalBackgroundLoadPriority != UnityEngine.ThreadPriority.Low)
            {
                // Async asset integration is frame-budgeted; preserve texture and mesh quality.
                Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.Low;
                modifiedBackgroundLoadPriority = true;
            }
            previousInfoLogStackTrace = Application.GetStackTraceLogType(LogType.Log);
            if (previousInfoLogStackTrace != StackTraceLogType.None)
            {
                Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                infoLogStackTraceOverridden = true;
            }
#endif
            sampleAt = Time.unscaledTime + ReportInterval;
            Application.lowMemory += OnLowMemory;
            Application.logMessageReceived += OnLog;
            try
            {
                string folder = Path.Combine(Application.persistentDataPath, "AarisDiagnostics");
                logPath = Path.Combine(folder, "current.jsonl");
#if UNITY_ANDROID && !UNITY_EDITOR
                // Avoid synchronous flash I/O before the first rendered frame.
                // The initial report queues behind file rotation.
                string initialPath = logPath;
                pendingLogWrite = Task.Run(() => InitializeLogFile(initialPath));
#else
                string startupError = InitializeLogFile(logPath);
                if (startupError != null) StopLogging(startupError);
#endif
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            { Debug.LogWarning("Aaris diagnostics file unavailable: " + e.Message); logPath = null; }
            Record("start");
        }

        private IEnumerator Start()
        {
            // DO NOT re-upload or change readability of TerrainData control
            // splatmaps on Android. Unity owns these GPU textures. The scene's
            // serialized flag might still be true in older projects; ignore it
            // rather than changing the four native TerrainData assets or
            // forcing a platform-specific renderer/material substitution.
            // Choose the saved device policy before submitting the first
            // world frame so startup does not build two different render states.
            ConfigureOceanShadowBudget();
            ConfigureTerrainInstanceRenderer();
#if UNITY_ANDROID && !UNITY_EDITOR
            ConfigureAndroidQuality();
#endif
            yield return null; // Audit after URP has drawn the first terrain frame.
#if UNITY_ANDROID && !UNITY_EDITOR
            startupDrop = FindFirstObjectByType<AarisOfflineDrop>();
#endif
            if (releaseTerrainPaintCpuCopies)
                Debug.LogWarning("AARIS_TERRAIN_PAINT_RELEASE_DISABLED: preserve original alphamap GPU uploads.");
            AuditNativeTerrain();
            Record("terrain-render-audit");
        }

        // The 2 km visual-only ocean is a flat water surface, not a shadow caster.
        // Disable both shadow passes once before the first camera frame; keep the
        // authored mesh, shader, materials and terrain assets untouched.
        private void ConfigureOceanShadowBudget()
        {
            GameObject ocean = GameObject.Find("Ocean_VisualOnly");
            if (ocean == null) return;
            oceanShadowRenderer = ocean.GetComponent<MeshRenderer>();
            if (oceanShadowRenderer == null) return;
            originalOceanCasting = oceanShadowRenderer.shadowCastingMode;
            originalOceanReceiving = oceanShadowRenderer.receiveShadows;
            if (originalOceanCasting != UnityEngine.Rendering.ShadowCastingMode.Off)
            {
                oceanShadowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                changedOceanCasting = true;
            }
            if (originalOceanReceiving)
            {
                oceanShadowRenderer.receiveShadows = false;
                changedOceanReceiving = true;
            }
        }

        private void ConfigureTerrainInstanceRenderer()
        {
            // Unity 6 defaults Terrain.drawInstanced to false. Enabling the
            // built-in instance renderer preserves authored heightmaps/layers
            // while moving the four native tiles onto the instanced terrain
            // path. This is runtime-only and never dirties TerrainData assets.
            if (!SystemInfo.supportsInstancing) return;
            if (terrains == null || terrains.Length == 0)
                terrains = Terrain.activeTerrains;
            if (terrains == null) return;
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain tile = terrains[i];
                if (tile != null && !tile.drawInstanced)
                    tile.drawInstanced = true;
            }
        }

        private void AuditNativeTerrain()
        {
            if (terrains == null || terrains.Length == 0)
                terrains = Terrain.activeTerrains;
            auditedTiles = visibleHeightmaps = paintedTiles = alphamapCount =
                invalidTerrainMaterials = 0;
            Shader lit = Shader.Find("Universal Render Pipeline/Terrain/Lit");
            terrainLitAvailable = lit != null && lit.isSupported;
            if (terrains != null)
            {
                for (int i = 0; i < terrains.Length; i++)
                {
                    Terrain tile = terrains[i];
                    if (tile == null || tile.terrainData == null) continue;
                    auditedTiles++;
                    if (tile.gameObject.activeInHierarchy && tile.enabled && tile.drawHeightmap)
                        visibleHeightmaps++;
                    // Diagnostics need counts, not Unity's texture objects.
                    // Avoid materializing all control-map Texture2D references
                    // immediately after the first Android terrain frame.
                    var layers = tile.terrainData.terrainLayers;
                    int controlCount = tile.terrainData.alphamapTextureCount;
                    if (layers != null && layers.Length > 0 && controlCount > 0)
                        paintedTiles++;
                    alphamapCount += controlCount;
                    Material material = tile.materialTemplate;
                    if (material != null && (material.shader == null || !material.shader.isSupported))
                        invalidTerrainMaterials++;
                }
            }
            // One bounded diagnostic on startup, not a per-frame scan.
            Debug.Log("AARIS_TERRAIN_RENDER_AUDIT" +
                " tiles=" + auditedTiles +
                " visible_heightmaps=" + visibleHeightmaps +
                " painted_tiles=" + paintedTiles +
                " alphamaps=" + alphamapCount +
                " invalid_materials=" + invalidTerrainMaterials +
                " urp_terrain_lit=" + (terrainLitAvailable ? "supported" : "missing") +
                " instancing=" + (terrains != null && Array.TrueForAll(terrains,
                    t => t == null || t.drawInstanced) ? "enabled" : "partial") +
                " api=" + SystemInfo.graphicsDeviceType);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Runs once after URP initializes. The source pipeline asset and terrain
        // materials stay untouched; only this Android player's runtime quality changes.
        private void ConfigureAndroidQuality()
        {
            // Non-resolution low-RAM controls (mip residency, shadow range,
            // foliage distance and wind) remain valuable even when dynamic
            // render-scale adaptation is disabled by the native-quality policy.
            // RAM is not a GPU score: inexpensive Mali-G52 phones can have
            // 4 GB and still struggle with thousands of windy SpeedTrees.
            // Enable the constrained-GPU path without blurring stronger GPUs.
            int memoryMB = SystemInfo.systemMemorySize;
            bool constrainedGraphics = memoryMB <= 2304 || IsBudgetAndroidGpu();
            if (memoryMB <= 0) return;
            androidPipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (androidPipeline == null) return;
            originalRenderScale = androidPipeline.renderScale;
            lastAppliedRenderScale = originalRenderScale;
            lastTargetFps = Application.targetFrameRate;
            lastEffectiveTargetFps = EffectiveAndroidTargetFps(lastTargetFps);
            // Only budget phones need extra foliage/shadow restrictions.
            // All Android phones can use the sustained-frame GPU governor.
            if (memoryMB > 3072 && !constrainedGraphics) return;
            // Budget Android GPUs can spend significant bandwidth on 4x MSAA.
            // Preserve native resolution and 2x edge smoothing when needed.
            if (constrainedGraphics)
            {
                originalMsaaSamples = androidPipeline.msaaSampleCount;
                appliedMsaaSamples = Mathf.Min(originalMsaaSamples, 2);
                if (appliedMsaaSamples < originalMsaaSamples)
                {
                    androidPipeline.msaaSampleCount = appliedMsaaSamples;
                    modifiedMsaaSamples = true;
                    Debug.Log("AARIS_ANDROID_MSAA_BUDGET original=" +
                        originalMsaaSamples + " applied=" + appliedMsaaSamples);
                }
            }
            // Mipmap streaming was already enabled by the saved QualitySettings.
            // Bound its texture residency on low-RAM phones without modifying
            // the four native TerrainData control textures or their readability.
            float mipBudgetMB = memoryMB <= 2304 ? 160f : 224f;
            originalMipBudgetMB = QualitySettings.streamingMipmapsMemoryBudget;
            appliedMipBudgetMB = Mathf.Min(originalMipBudgetMB, mipBudgetMB);
            if (appliedMipBudgetMB < originalMipBudgetMB)
            {
                QualitySettings.streamingMipmapsMemoryBudget = appliedMipBudgetMB;
                modifiedMipBudget = true;
            }
            // The authored URP shadow distance spans 1000 m of a 2 km map.
            // Rendering distant tree/house shadow casters consumes fill,
            // draw and bandwidth budget on tile-based mobile GPUs. Keep
            // nearby buildings and foliage grounded by real shadows, but
            // avoid drawing shadow casters for most of the distant map.
            // Keep the 4 native cascades / atlas and all source assets intact.
            originalShadowDistance = androidPipeline.shadowDistance;
            float mobileShadowDistance = constrainedGraphics ? 120f : 240f;
            appliedShadowDistance = Mathf.Min(originalShadowDistance, mobileShadowDistance);
            if (appliedShadowDistance < originalShadowDistance)
            {
                androidPipeline.shadowDistance = appliedShadowDistance;
                modifiedShadowDistance = true;
            }
            if (constrainedGraphics)
            {
                // Four cascades render distant shadow casters repeatedly. Keep
                // two near-camera cascades with real shadows for players,
                // buildings and foliage, with no edits to the URP source asset.
                originalShadowCascades = androidPipeline.shadowCascadeCount;
                appliedShadowCascades = Mathf.Min(originalShadowCascades, 2);
                if (appliedShadowCascades < originalShadowCascades)
                {
                    androidPipeline.shadowCascadeCount = appliedShadowCascades;
                    modifiedShadowCascades = true;
                }
                ConfigureLowMemoryVegetation();
            }
        }

        // Unity 6's frame timing API is only active on supported devices/builds.
        // Never infer GPU load from Time.unscaledDeltaTime: that also includes
        // scripting, asset loading, physics, device pacing and OS scheduling.
        private void SampleMobileFrameTiming()
        {
            if (++timingFrameCounter < 15) return;
            timingFrameCounter = 0;
            if (!FrameTimingManager.IsFeatureEnabled()) return;
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1u, mobileFrameTimings) == 0u)
                return;
            FrameTiming timing = mobileFrameTimings[0];
            // Some Android graphics backends report no separate render-thread
            // timer (0 ms on single-threaded rendering). Rejecting the whole
            // sample hides valid GPU + main-thread timings and can make the
            // governor downscale a CPU-bound scene unnecessarily.
            double cpuMain = Math.Max(0d, timing.cpuMainThreadFrameTime);
            double cpuRender = Math.Max(0d, timing.cpuRenderThreadFrameTime);
            if (timing.gpuFrameTime <= 0d || (cpuMain <= 0d && cpuRender <= 0d))
                return;
            double cpuWork = Math.Max(cpuMain, cpuRender);
            timingSamples++;
            gpuFrameTimeTotalMs += timing.gpuFrameTime;
            cpuMainTimeTotalMs += cpuMain;
            cpuRenderTimeTotalMs += cpuRender;
            if (timing.gpuFrameTime >= cpuWork * 1.1d)
                gpuLimitedSamples++;
            else if (cpuWork >= timing.gpuFrameTime * 1.2d)
                cpuLimitedSamples++;
        }

        private static bool IsBudgetAndroidGpu()
        {
            // A narrow, auditable list: many 4 GB budget gaming phones still
            // use these limited tile-based GPUs. Avoid RAM-only classification.
            string gpu = SystemInfo.graphicsDeviceName ?? "";
            // On some Android drivers the same entry-level G57 is reported
            // as "Mali-G57 MC2", "Mali G57 MC2" or "Mali-G57 MP2".
            // Match only the small 1/2-core variants: broad "Mali-G57"
            // would unnecessarily downgrade stronger MC3/MC4 phones.
            // This runs only at quality setup, never on the render hotpath.
            string normalizedGpu = gpu.Replace('-', ' ').Replace('_', ' ');
            // Match GPU + core count together: MC21 or an unrelated driver
            // token must not masquerade as a Mali-G57 MC2.
            bool smallG57 = HasExactGpuModel(normalizedGpu, "Mali G57 MC1") ||
                            HasExactGpuModel(normalizedGpu, "Mali G57 MC2") ||
                            HasExactGpuModel(normalizedGpu, "Mali G57 MP1") ||
                            HasExactGpuModel(normalizedGpu, "Mali G57 MP2");
            // GPU vendors report equivalent chips with dashes, spaces and
            // underscores. Reuse the normalized name for the older Mali line
            // as well; otherwise "Mali G52" misses the same budget as
            // "Mali-G52" (likewise "Mali-G51" and "Mali-G31").
            bool legacyMali = HasExactGpuModel(normalizedGpu, "Mali G52") ||
                              HasExactGpuModel(normalizedGpu, "Mali G51") ||
                              HasExactGpuModel(normalizedGpu, "Mali G31");
            // Android's trademark suffix can appear between Adreno and 610.
            // Keep these exact low-end matches, not a blanket Adreno rule.
            // False prefix matches on newer devices needlessly lower quality.
            bool smallAdreno610 =
                HasExactGpuModel(gpu, "Adreno 610") ||
                HasExactGpuModel(gpu, "Adreno (TM) 610");
            bool smallPowerVr =
                HasExactGpuModel(gpu, "PowerVR GE8320") ||
                HasExactGpuModel(gpu, "PowerVR Rogue GE8320");
            return smallG57 || legacyMali || smallPowerVr || smallAdreno610;
        }

        // Vehicle streaming must use the SAME GPU classification as terrain,
        // shadows and vegetation. A 4 GB Adreno 610 device is still GPU-limited
        // even though its physical memory exceeds the 3 GB vehicle threshold.
        internal static bool UseConstrainedGpuStreamingBudget()
        {
            return IsBudgetAndroidGpu();
        }

        // Model prefixes are not interchangeable: Mali-G520 is not G52,
        // and Mali-G570 MC2 is not G57 MC2. Skip accidental substring hits
        // while accepting vendor punctuation and suffixes such as "MC2".
        // One-time setup-only helper; never runs per rendered frame.
        private static bool HasExactGpuModel(string description, string model)
        {
            // Driver labels can list an invalid prefix before a later exact
            // GPU name; scan every candidate rather than only the first.
            int start = 0;
            while (start <= description.Length - model.Length)
            {
                int index = description.IndexOf(model, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0) return false;
                int end = index + model.Length;
                if ((index == 0 || !char.IsLetterOrDigit(description[index - 1])) &&
                    (end == description.Length || !char.IsLetterOrDigit(description[end])))
                    return true;
                start = index + 1;
            }
            return false;
        }

        // One-time, Android-only vegetation work: static wind is much cheaper
        // than animated leaf/grass vertex deformation on weak mobile GPUs.
        // Preserve close cover, native tree count and full-resolution materials.
        private void ConfigureLowMemoryVegetation()
        {
            mobileFoliageTiles = terrains != null && terrains.Length > 0
                ? terrains : Terrain.activeTerrains;
            int count = mobileFoliageTiles == null ? 0 : mobileFoliageTiles.Length;
            originalDetailDistances = new float[count];
            appliedDetailDistances = new float[count];
            originalBillboardDistances = new float[count];
            appliedBillboardDistances = new float[count];
            originalTreeDistances = new float[count];
            appliedTreeDistances = new float[count];
            originalFullLodTreeCounts = new int[count];
            mobileWindData = new TerrainData[count];
            originalGrassWindStrength = new float[count];
            for (int i = 0; i < count; i++)
            {
                Terrain tile = mobileFoliageTiles[i];
                if (tile == null) continue;
                originalDetailDistances[i] = tile.detailObjectDistance;
                originalBillboardDistances[i] = tile.treeBillboardDistance;
                originalTreeDistances[i] = tile.treeDistance;
                originalFullLodTreeCounts[i] = tile.treeMaximumFullLODCount;
                // Keep all grass within 80 m, but avoid processing small
                // vegetation on the outer 80-100 m ring of each camera view.
                appliedDetailDistances[i] = Mathf.Min(originalDetailDistances[i], 80f);
                appliedBillboardDistances[i] = Mathf.Min(originalBillboardDistances[i], 85f);
                // 850m draw reach means far tree billboards are still submitted
                // across most of the 2km world. Bound only weak Android GPUs,
                // keep the full authored forest close to the player and all
                // existing terrain/tree collision data unchanged.
                float treeDistanceLimit = SystemInfo.systemMemorySize <= 2304
                    ? 450f : 600f;
                appliedTreeDistances[i] = Mathf.Min(originalTreeDistances[i],
                    treeDistanceLimit);
                if (appliedDetailDistances[i] < originalDetailDistances[i])
                    tile.detailObjectDistance = appliedDetailDistances[i];
                if (appliedBillboardDistances[i] < originalBillboardDistances[i])
                    tile.treeBillboardDistance = appliedBillboardDistances[i];
                if (appliedTreeDistances[i] < originalTreeDistances[i])
                    tile.treeDistance = appliedTreeDistances[i];
                // Keep authored tree coverage and the near billboard transition,
                // while capping full-detail meshes in dense groves on weak GPUs.
                if (originalFullLodTreeCounts[i] > LowMemoryFullLodTreeLimit)
                    tile.treeMaximumFullLODCount = LowMemoryFullLodTreeLimit;
                TerrainData data = tile.terrainData;
                if (data == null) continue;
                mobileWindData[i] = data;
                originalGrassWindStrength[i] = data.wavingGrassStrength;
                if (originalGrassWindStrength[i] > 0f)
                {
                    data.wavingGrassStrength = 0f;
                    suppressedGrassTiles++;
                }
            }
            // A WindZone inherits Component (not Behaviour), so wind is
            // disabled via its force fields, never by toggling enabled.
            // This leaves any other components on the same GameObject intact.
            mobileWindZones = UnityEngine.Object.FindObjectsByType<WindZone>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            int zoneCount = mobileWindZones.Length;
            originalWindMain = new float[zoneCount];
            originalWindTurbulence = new float[zoneCount];
            originalWindPulseMagnitude = new float[zoneCount];
            originalWindPulseFrequency = new float[zoneCount];
            for (int i = 0; i < zoneCount; i++)
            {
                WindZone zone = mobileWindZones[i];
                if (zone == null) continue;
                originalWindMain[i] = zone.windMain;
                originalWindTurbulence[i] = zone.windTurbulence;
                originalWindPulseMagnitude[i] = zone.windPulseMagnitude;
                originalWindPulseFrequency[i] = zone.windPulseFrequency;
                if (!Mathf.Approximately(zone.windMain, 0f) ||
                    !Mathf.Approximately(zone.windTurbulence, 0f) ||
                    !Mathf.Approximately(zone.windPulseMagnitude, 0f))
                    suppressedWindZones++;
                zone.windMain = 0f;
                zone.windTurbulence = 0f;
                zone.windPulseMagnitude = 0f;
                zone.windPulseFrequency = 0f;
            }
            Debug.Log("AARIS_ANDROID_LOW_RAM_VEGETATION gpu=" +
                      SystemInfo.graphicsDeviceName + " tiles=" + count +
                      " grass_wind_disabled=" + suppressedGrassTiles +
                      " wind_zones_disabled=" + suppressedWindZones +
                      " details_max_m=80 tree_billboards_m=85 tree_full_lod_cap=" +
                      LowMemoryFullLodTreeLimit +
                      " shadow_max_m=" + appliedShadowDistance +
                      " cascades=" + appliedShadowCascades);
        }

        // Android rounds requested FPS down to a divisor of the display rate.
        // For example, a 90 Hz screen requested at 60 FPS actually paces at
        // 45 FPS, so measuring its 22 ms frames against 16.7 ms would wrongly
        // blur the world even though the GPU is meeting the device's pacing.
        private static float EffectiveAndroidTargetFps(int requestedFps)
        {
            if (requestedFps <= 0) return 30f; // Android's -1 default.
            float refreshHz = (float)Screen.currentResolution.refreshRateRatio.value;
            if (refreshHz <= 0f) return requestedFps;
            int divisor = Mathf.Max(1, Mathf.CeilToInt(refreshHz / requestedFps));
            return refreshHz / divisor;
        }

        // Two sustained slow windows to lower GPU fill cost, four stable windows
        // to recover detail. Outlier hitches (asset loads, GC, OS scheduling)
        // must not blur the image when most frames still meet the budget.
        private void TuneAndroidResolution(Sample sample)
        {
            if (androidPipeline == null || !adaptiveAndroidResolution) return;
            int targetFps = Application.targetFrameRate;
            if (targetFps <= 0) return;
            float effectiveFps = EffectiveAndroidTargetFps(targetFps);
            // Android can change display refresh during play (e.g. 90 Hz to
            // 60 Hz). Never combine consecutive slow windows measured against
            // different frame budgets or blur the world on the transition.
            if (lastTargetFps != targetFps ||
                Mathf.Abs(lastEffectiveTargetFps - effectiveFps) > 0.5f)
            {
                lastTargetFps = targetFps;
                lastEffectiveTargetFps = effectiveFps;
                slowIntervals = steadyIntervals = 0;
                // Old ring-buffer frames were paced against another budget.
                // Refill from the new refresh mode before making a decision.
                frameIndex = frameCount = 0;
                sample.renderScale = androidPipeline.renderScale;
                return;
            }
            float budgetMs = 1000f / effectiveFps;
            if (sample.p95FrameMs > budgetMs * 1.20f &&
                sample.p75FrameMs > budgetMs * 1.06f)
            {
                slowIntervals++;
                steadyIntervals = 0;
            }
            else if (sample.p95FrameMs < budgetMs * 1.08f &&
                     sample.p99FrameMs < budgetMs * 1.40f)
            {
                steadyIntervals++;
                slowIntervals = 0;
            }
            else
            {
                slowIntervals = steadyIntervals = 0;
            }

            float current = androidPipeline.renderScale;
            float floor = Mathf.Min(originalRenderScale,
                Mathf.Max(0.82f, Mathf.Clamp(minimumAndroidRenderScale, 0.65f, 1f)));
            if (slowIntervals >= 2)
            {
                bool reliableTimings = sample.gpuTimingSamples >= 4;
                bool measuredCpuBound = reliableTimings &&
                    sample.cpuLimitedSamples >= 3 &&
                    sample.cpuLimitedSamples >= sample.gpuLimitedSamples * 2;
                bool measuredGpuBound = reliableTimings &&
                    sample.gpuLimitedSamples >= 3 &&
                    sample.gpuLimitedSamples >= sample.cpuLimitedSamples * 2;
                // CPU-bound or mixed workloads should not lose clarity.
                if (measuredCpuBound || (reliableTimings && !measuredGpuBound))
                {
                    slowIntervals = 0;
                    sample.renderScale = androidPipeline.renderScale;
                    return;
                }
                if (!reliableTimings)
                {
                    // No GPU timer: only a small fallback reduction after
                    // three slow windows, to preserve legibility and detail.
                    if (slowIntervals < 3) return;
                    floor = Mathf.Max(floor, originalRenderScale * 0.92f);
                }
                float reduced = Mathf.Max(floor, current - 0.05f);
                if (reduced < current - 0.001f)
                {
                    androidPipeline.renderScale = reduced;
                    lastAppliedRenderScale = reduced;
                }
                slowIntervals = 0;
            }
            else if (steadyIntervals >= 4)
            {
                float recovered = Mathf.Min(originalRenderScale, current + 0.025f);
                if (recovered > current + 0.001f)
                {
                    androidPipeline.renderScale = recovered;
                    lastAppliedRenderScale = recovered;
                }
                steadyIntervals = 0;
            }
            sample.renderScale = androidPipeline.renderScale;
        }
#endif

        private void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            SampleMobileFrameTiming();
#endif
            float dt = Time.unscaledDeltaTime;
            if (dt > 0)
            {
                frameTimes[frameIndex] = dt;
                frameIndex = (frameIndex + 1) % frameTimes.Length;
                frameCount = Mathf.Min(frameCount + 1, frameTimes.Length);
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            // Record real multi-second frames for field triage without doing disk
            // I/O on the game thread. Rate-limit bursts after app resume.
            if (dt >= 0.75f && Time.unscaledTime >= nextLongFrameReportAt)
            {
                nextLongFrameReportAt = Time.unscaledTime + 5f;
                Record("frame-stall");
            }
#endif
            if (lowMemoryPending)
            {
                lowMemoryPending = false;
                Record("low-memory");
                if (Time.unscaledTime >= unloadAfter && (unloading == null || unloading.isDone))
                {
                    unloading = Resources.UnloadUnusedAssets();
                    unloadAfter = Time.unscaledTime + 30;
                }
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            // The regular 15-second ring no longer contains the opening frames.
            // Keep three bounded snapshots for comparing cold and warm launches.
            if (startupSampleIndex < 3)
            {
                float seconds = Time.unscaledTime - startupTime;
                float threshold = startupSampleIndex == 0 ? 1f : startupSampleIndex == 1 ? 3f : 8f;
                if (seconds >= threshold)
                {
                    startupSampleIndex++;
                    Record("startup");
                }
            }
#endif
            if (Time.unscaledTime >= sampleAt)
            {
                Record("sample");
                sampleAt = Time.unscaledTime + ReportInterval;
            }
        }

        private void OnLowMemory() { lowMemoryPending = true; }
        private void OnApplicationFocus(bool focused)
        {
            // Android may regain focus after a system overlay without pause.
            // Reset stale frame-time samples before the next quality decision.
            if (focused) OnApplicationPause(false);
        }
        private void OnApplicationPause(bool paused)
        {
            if (paused) Record("pause");
            else
            {
                // Do not let the resumed frame or old device-time samples
                // trigger an inappropriate resolution drop after backgrounding.
                frameIndex = frameCount = 0;
                sampleAt = Time.unscaledTime + ReportInterval;
#if UNITY_ANDROID && !UNITY_EDITOR
                slowIntervals = steadyIntervals = 0;
                // Android background time and stale GPU/CPU classifications do
                // not belong to the next post-resume render-quality window.
                // Otherwise the governor can incorrectly reduce scene clarity
                // even though only the resumed frame was slow.
                timingFrameCounter = timingSamples = gpuLimitedSamples = cpuLimitedSamples = 0;
                gpuFrameTimeTotalMs = cpuMainTimeTotalMs = cpuRenderTimeTotalMs = 0d;
#endif
            }
        }
        private void OnApplicationQuit() { Record("quit"); }
        private void OnDestroy()
        {
            Application.lowMemory -= OnLowMemory;
            Application.logMessageReceived -= OnLog;
            // Restore only properties still holding the values that we set.
            // Other water/quality controllers retain their later changes.
            if (oceanShadowRenderer != null)
            {
                if (changedOceanCasting &&
                    oceanShadowRenderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off)
                    oceanShadowRenderer.shadowCastingMode = originalOceanCasting;
                if (changedOceanReceiving && !oceanShadowRenderer.receiveShadows)
                    oceanShadowRenderer.receiveShadows = originalOceanReceiving;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            // Undo only our own change. Other subsystems might deliberately
            // customize the log stack trace policy while this scene is alive.
            if (infoLogStackTraceOverridden &&
                Application.GetStackTraceLogType(LogType.Log) == StackTraceLogType.None)
                Application.SetStackTraceLogType(LogType.Log, previousInfoLogStackTrace);
            if (modifiedBackgroundLoadPriority &&
                Application.backgroundLoadingPriority == UnityEngine.ThreadPriority.Low)
                Application.backgroundLoadingPriority = originalBackgroundLoadPriority;
            // URP pipeline assets and QualitySettings survive scene reloads.
            // Restore only values still owned by this instance; avoid undoing
            // a deliberate change made by another quality controller.
            if (androidPipeline != null &&
                Mathf.Approximately(androidPipeline.renderScale, lastAppliedRenderScale))
                androidPipeline.renderScale = originalRenderScale;
            if (modifiedMsaaSamples && androidPipeline != null &&
                androidPipeline.msaaSampleCount == appliedMsaaSamples)
                androidPipeline.msaaSampleCount = originalMsaaSamples;
            if (modifiedMipBudget &&
                Mathf.Approximately(QualitySettings.streamingMipmapsMemoryBudget, appliedMipBudgetMB))
                QualitySettings.streamingMipmapsMemoryBudget = originalMipBudgetMB;
            // Do not leave a shortened shadow range across scene reloads.
            // Nor overwrite an unrelated controller's later quality choice.
            if (modifiedShadowDistance && androidPipeline != null &&
                Mathf.Approximately(androidPipeline.shadowDistance, appliedShadowDistance))
                androidPipeline.shadowDistance = originalShadowDistance;
            if (modifiedShadowCascades && androidPipeline != null &&
                androidPipeline.shadowCascadeCount == appliedShadowCascades)
                androidPipeline.shadowCascadeCount = originalShadowCascades;
            // Undo only properties that still equal our applied values. Other
            // quality controllers and scene reloads retain ownership.
            if (mobileWindZones != null)
                for (int i = 0; i < mobileWindZones.Length; i++)
                {
                    WindZone zone = mobileWindZones[i];
                    if (zone == null) continue;
                    // Leave any subsequent quality controller's changes alone.
                    if (Mathf.Approximately(zone.windMain, 0f) &&
                        Mathf.Approximately(zone.windTurbulence, 0f) &&
                        Mathf.Approximately(zone.windPulseMagnitude, 0f) &&
                        Mathf.Approximately(zone.windPulseFrequency, 0f))
                    {
                        zone.windMain = originalWindMain[i];
                        zone.windTurbulence = originalWindTurbulence[i];
                        zone.windPulseMagnitude = originalWindPulseMagnitude[i];
                        zone.windPulseFrequency = originalWindPulseFrequency[i];
                    }
                }
            if (mobileFoliageTiles != null)
                for (int i = 0; i < mobileFoliageTiles.Length; i++)
                {
                    Terrain tile = mobileFoliageTiles[i];
                    if (tile == null) continue;
                    if (appliedDetailDistances[i] < originalDetailDistances[i] &&
                        Mathf.Approximately(tile.detailObjectDistance, appliedDetailDistances[i]))
                        tile.detailObjectDistance = originalDetailDistances[i];
                    if (appliedBillboardDistances[i] < originalBillboardDistances[i] &&
                        Mathf.Approximately(tile.treeBillboardDistance, appliedBillboardDistances[i]))
                        tile.treeBillboardDistance = originalBillboardDistances[i];
                    if (appliedTreeDistances[i] < originalTreeDistances[i] &&
                        Mathf.Approximately(tile.treeDistance, appliedTreeDistances[i]))
                        tile.treeDistance = originalTreeDistances[i];
                    if (originalFullLodTreeCounts[i] > LowMemoryFullLodTreeLimit &&
                        tile.treeMaximumFullLODCount == LowMemoryFullLodTreeLimit)
                        tile.treeMaximumFullLODCount = originalFullLodTreeCounts[i];
                    if (mobileWindData[i] != null && originalGrassWindStrength[i] > 0f &&
                        Mathf.Approximately(mobileWindData[i].wavingGrassStrength, 0f))
                        mobileWindData[i].wavingGrassStrength = originalGrassWindStrength[i];
                }
#endif
        }

        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            string detail = message + "\n" + stackTrace;
            lastError = detail.Length > 1536 ? detail.Substring(0, 1536) : detail;
        }

        [Serializable]
        private sealed class Sample
        {
            public string reason, utc, version, device, gpu, api, lastError;
            public int systemRamMB, targetFps, warningCount, framesOver50Ms, frameSamples;
            public string dropPhase;
            public bool weaponCatalogReady;
            public float processSeconds, maxFrameMs;
            public float effectiveTargetFps;
            public int gpuTimingSamples, gpuLimitedSamples, cpuLimitedSamples;
            public float meanGpuFrameMs, meanCpuMainMs, meanCpuRenderMs;
            public float seconds, averageFps, p75FrameMs, p95FrameMs, p99FrameMs, renderScale = 1f;
            public long unityAllocatedBytes, unityReservedBytes, managedBytes, androidPssKB;
            public ulong textureBytes, desiredTextureBytes;
            public long terrainCpuPaintBytesReleased;
            public int terrainTiles, visibleTerrainHeightmaps, paintedTerrainTiles;
            public int terrainAlphamaps, unsupportedTerrainMaterials;
            public bool terrainLitSupported;
            public int lowMemoryWindZonesDisabled, lowMemoryGrassTilesDisabled;
            public float lowMemoryShadowDistance;
        }
        private int warningCount;
        private void Record(string reason)
        {
            if (reason == "low-memory") warningCount++;
            float total = 0;
            int framesOver50Ms = 0;
            for (int i = 0; i < frameCount; i++)
            {
                float frameTime = frameTimes[i];
                total += frameTime;
                sortedTimes[i] = frameTime;
                if (frameTime > .05f) framesOver50Ms++;
            }
            Array.Sort(sortedTimes, 0, frameCount);
            var s = new Sample {
                reason = reason, utc = DateTime.UtcNow.ToString("O"), version = Application.version,
                device = SystemInfo.deviceModel, gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(),
                systemRamMB = SystemInfo.systemMemorySize, targetFps = Application.targetFrameRate,
                seconds = Time.unscaledTime, averageFps = total > 0 ? frameCount / total : 0,
                p75FrameMs = frameCount > 0 ? sortedTimes[Mathf.CeilToInt(frameCount * .75f) - 1] * 1000 : 0,
                p95FrameMs = frameCount > 0 ? sortedTimes[Mathf.Clamp(Mathf.CeilToInt(frameCount * .95f) - 1, 0, frameCount - 1)] * 1000 : 0,
                p99FrameMs = frameCount > 0 ? sortedTimes[Mathf.CeilToInt(frameCount * .99f) - 1] * 1000 : 0,
                framesOver50Ms = framesOver50Ms, frameSamples = frameCount,
                processSeconds = Time.realtimeSinceStartup,
                maxFrameMs = frameCount > 0 ? sortedTimes[frameCount - 1] * 1000 : 0,
                weaponCatalogReady = AarisWeaponCatalog.Ready,
                unityAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong(), unityReservedBytes = Profiler.GetTotalReservedMemoryLong(),
                managedBytes = GC.GetTotalMemory(false), textureBytes = Texture.currentTextureMemory,
                desiredTextureBytes = Texture.desiredTextureMemory, terrainCpuPaintBytesReleased = releasedPaintBytes,
                terrainTiles = auditedTiles, visibleTerrainHeightmaps = visibleHeightmaps,
                paintedTerrainTiles = paintedTiles, terrainAlphamaps = alphamapCount,
                unsupportedTerrainMaterials = invalidTerrainMaterials,
                terrainLitSupported = terrainLitAvailable,
                androidPssKB = -1, warningCount = warningCount, lastError = lastError
            };
#if UNITY_ANDROID && !UNITY_EDITOR
            // Android Debug.getPss() synchronously walks process memory and
            // can stall the foreground game, especially on low-memory events.
            // Do not query it on Unity's main thread. The external Android
            // device profiler supplies process PSS when diagnosing OOM/LMK.
            // -1 explicitly means unavailable, not zero memory usage.
            s.androidPssKB = -1;
            if (startupDrop != null) s.dropPhase = startupDrop.Phase.ToString();
            s.lowMemoryWindZonesDisabled = suppressedWindZones;
            s.lowMemoryGrassTilesDisabled = suppressedGrassTiles;
            s.lowMemoryShadowDistance = appliedShadowDistance;
            s.effectiveTargetFps = EffectiveAndroidTargetFps(s.targetFps);
            s.gpuTimingSamples = timingSamples;
            s.gpuLimitedSamples = gpuLimitedSamples;
            s.cpuLimitedSamples = cpuLimitedSamples;
            if (timingSamples > 0)
            {
                s.meanGpuFrameMs = (float)(gpuFrameTimeTotalMs / timingSamples);
                s.meanCpuMainMs = (float)(cpuMainTimeTotalMs / timingSamples);
                s.meanCpuRenderMs = (float)(cpuRenderTimeTotalMs / timingSamples);
            }
            if (reason == "sample")
            {
                if (frameCount >= 30) TuneAndroidResolution(s);
                timingSamples = gpuLimitedSamples = cpuLimitedSamples = 0;
                gpuFrameTimeTotalMs = cpuMainTimeTotalMs = cpuRenderTimeTotalMs = 0d;
            }
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline)
                s.renderScale = pipeline.renderScale;
#endif
            LatestReport = JsonUtility.ToJson(s);
            if (logPath == null) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (pendingLogWrite != null)
            {
                // Never wait for storage from Unity's main thread. A blocked
                // diagnostics writer during Android pause/quit can otherwise
                // turn a temporary I/O delay into an application freeze/ANR.
                if (!pendingLogWrite.IsCompleted)
                {
                    if (reason != "pause" && reason != "quit" &&
                        reason != "low-memory" && reason != "frame-stall" &&
                        reason != "start")
                        return;
                }
                else
                {
                    string previousError;
                    try { previousError = pendingLogWrite.GetAwaiter().GetResult(); }
                    catch (Exception e) { previousError = e.GetType().Name; }
                    pendingLogWrite = null;
                    if (previousError != null)
                    {
                        StopLogging(previousError);
                        return;
                    }
                }
            }
            // Also enqueue the first start report after background rotation.
            // Keep the already-merged #679 urgent-event ordering.
            string path = logPath, report = LatestReport;
            Task<string> previous = pendingLogWrite;
            pendingLogWrite = previous == null
                ? Task.Run(() => AppendReport(path, report))
                : previous.ContinueWith(completed =>
                {
                    if (completed.IsFaulted)
                        return "Previous diagnostics write failed: " +
                            completed.Exception.GetBaseException().GetType().Name;
                    if (completed.IsCanceled)
                        return "Previous diagnostics write canceled";
                    if (completed.Result != null) return completed.Result;
                    return AppendReport(path, report);
                }, TaskScheduler.Default);
            return;
#else
            string error = AppendReport(logPath, LatestReport);
            if (error != null) StopLogging(error);
#endif
        }

        // Called on a background worker in Android, before any report append.
        // This function must not use UnityEngine APIs.
        private static string InitializeLogFile(string path)
        {
            try
            {
                string folder = Path.GetDirectoryName(path);
                Directory.CreateDirectory(folder);
                string previous = Path.Combine(folder, "previous.jsonl");
                if (File.Exists(path)) File.Copy(path, previous, true);
                File.WriteAllText(path, "");
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return e.Message;
            }
        }

        private static string AppendReport(string path, string report)
        {
            try
            {
                if (new FileInfo(path).Length > 256 * 1024) File.WriteAllText(path, "");
                File.AppendAllText(path, report + "\n");
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return e.Message;
            }
        }

        private void StopLogging(string error)
        {
            Debug.LogWarning("Aaris diagnostics write stopped: " + error);
            logPath = null;
        }
    }
}
