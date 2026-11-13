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
        [Header("Android-only: conservative URP resolution adaptation")]
        [SerializeField] private bool adaptiveAndroidResolution = true;
        [SerializeField, Range(0.65f, 1f)] private float minimumAndroidRenderScale = 0.72f;
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
        private const float ReportInterval = 15f;
#if UNITY_ANDROID && !UNITY_EDITOR
        private long androidPssKB = -1;
        private Task<string> pendingLogWrite;
        private UniversalRenderPipelineAsset androidPipeline;
        private float originalRenderScale = 1f;
        private float lastAppliedRenderScale = 1f;
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
                Directory.CreateDirectory(folder);
                logPath = Path.Combine(folder, "current.jsonl");
                string previous = Path.Combine(folder, "previous.jsonl");
                if (File.Exists(logPath)) File.Copy(logPath, previous, true);
                File.WriteAllText(logPath, "");
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
            yield return null; // Let URP initialize its first terrain frame.
#if UNITY_ANDROID && !UNITY_EDITOR
            ConfigureAndroidQuality();
#endif
            if (releaseTerrainPaintCpuCopies)
                Debug.LogWarning("AARIS_TERRAIN_PAINT_RELEASE_DISABLED: preserve original alphamap GPU uploads.");
            AuditNativeTerrain();
            Record("terrain-render-audit");
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
                    var layers = tile.terrainData.terrainLayers;
                    var control = tile.terrainData.alphamapTextures;
                    if (layers != null && layers.Length > 0 && control != null && control.Length > 0)
                        paintedTiles++;
                    if (control != null) alphamapCount += control.Length;
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
                " api=" + SystemInfo.graphicsDeviceType);
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // Runs once after URP initializes. The source pipeline asset and terrain
        // materials stay untouched; only this Android player's runtime quality changes.
        private void ConfigureAndroidQuality()
        {
            if (!adaptiveAndroidResolution) return;
            // RAM is not a GPU score: inexpensive Mali-G52 phones can have
            // 4 GB and still struggle with thousands of windy SpeedTrees.
            // Enable the constrained-GPU path without blurring stronger GPUs.
            int memoryMB = SystemInfo.systemMemorySize;
            bool constrainedGraphics = memoryMB <= 2304 || IsBudgetAndroidGpu();
            if (memoryMB <= 0 || (memoryMB > 3072 && !constrainedGraphics)) return;
            androidPipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (androidPipeline == null) return;
            originalRenderScale = androidPipeline.renderScale;
            lastTargetFps = Application.targetFrameRate;
            lastEffectiveTargetFps = EffectiveAndroidTargetFps(lastTargetFps);
            // Pixel cost depends on the actual display size, not RAM alone.
            // On <=720p screens, the fixed low-RAM scale unnecessarily blurs
            // nearby terrain, foliage and building silhouettes. Restore some
            // native detail without raising the quality of 1080p+ devices.
            // The existing sustained frame-time governor can still lower this
            // scale if the GPU cannot keep up.
            bool compactDisplay = Screen.width > 0 && Screen.height > 0 &&
                (long)Screen.width * Screen.height <= 1280L * 720L;
            float initialScale = constrainedGraphics
                ? (compactDisplay ? 0.92f : 0.85f)
                : (compactDisplay ? 0.96f : 0.92f);
            androidPipeline.renderScale = Mathf.Min(originalRenderScale, initialScale);
            lastAppliedRenderScale = androidPipeline.renderScale;
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
            return gpu.IndexOf("Mali-G52", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   gpu.IndexOf("Mali-G51", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   gpu.IndexOf("Mali-G31", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   gpu.IndexOf("PowerVR GE8320", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   gpu.IndexOf("Adreno 610", StringComparison.OrdinalIgnoreCase) >= 0;
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
            mobileWindData = new TerrainData[count];
            originalGrassWindStrength = new float[count];
            for (int i = 0; i < count; i++)
            {
                Terrain tile = mobileFoliageTiles[i];
                if (tile == null) continue;
                originalDetailDistances[i] = tile.detailObjectDistance;
                originalBillboardDistances[i] = tile.treeBillboardDistance;
                // Keep all grass within 80 m, but avoid processing small
                // vegetation on the outer 80-100 m ring of each camera view.
                appliedDetailDistances[i] = Mathf.Min(originalDetailDistances[i], 80f);
                appliedBillboardDistances[i] = Mathf.Min(originalBillboardDistances[i], 85f);
                if (appliedDetailDistances[i] < originalDetailDistances[i])
                    tile.detailObjectDistance = appliedDetailDistances[i];
                if (appliedBillboardDistances[i] < originalBillboardDistances[i])
                    tile.treeBillboardDistance = appliedBillboardDistances[i];
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
                      " details_max_m=80 tree_billboards_m=85" +
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
                Mathf.Clamp(minimumAndroidRenderScale, 0.65f, 1f));
            if (slowIntervals >= 2)
            {
                // If actual Unity timings prove CPU work dominates, reducing
                // URP resolution cannot repair it and only damages clarity.
                // Unsupported GPU timers retain the old, conservative governor.
                bool measuredCpuBound = sample.gpuTimingSamples >= 4 &&
                    sample.cpuLimitedSamples >= 3 &&
                    sample.cpuLimitedSamples >= sample.gpuLimitedSamples * 2;
                if (measuredCpuBound)
                {
                    slowIntervals = 0;
                    sample.renderScale = androidPipeline.renderScale;
                    return;
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
            if (Time.unscaledTime >= sampleAt)
            {
                Record("sample");
                sampleAt = Time.unscaledTime + ReportInterval;
            }
        }

        private void OnLowMemory() { lowMemoryPending = true; }
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
#endif
            }
        }
        private void OnApplicationQuit() { Record("quit"); }
        private void OnDestroy()
        {
            Application.lowMemory -= OnLowMemory;
            Application.logMessageReceived -= OnLog;
#if UNITY_ANDROID && !UNITY_EDITOR
            // Undo only our own change. Other subsystems might deliberately
            // customize the log stack trace policy while this scene is alive.
            if (infoLogStackTraceOverridden &&
                Application.GetStackTraceLogType(LogType.Log) == StackTraceLogType.None)
                Application.SetStackTraceLogType(LogType.Log, previousInfoLogStackTrace);
            // URP pipeline assets and QualitySettings survive scene reloads.
            // Restore only values still owned by this instance; avoid undoing
            // a deliberate change made by another quality controller.
            if (androidPipeline != null &&
                Mathf.Approximately(androidPipeline.renderScale, lastAppliedRenderScale))
                androidPipeline.renderScale = originalRenderScale;
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
            public int systemRamMB, targetFps, warningCount, framesOver50Ms;
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
                framesOver50Ms = framesOver50Ms,
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
            // PSS is a synchronous kernel memory query. Keep it off the routine gameplay sample path.
            if (reason == "terrain-render-audit" || reason == "low-memory" || reason == "pause")
            {
                try { using (var debug = new AndroidJavaClass("android.os.Debug")) androidPssKB = debug.CallStatic<long>("getPss"); }
                catch (Exception e) { Debug.LogWarning("Android PSS unavailable: " + e.GetType().Name); }
            }
            s.androidPssKB = androidPssKB;
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
                // Keep at most one outstanding write. Pause/quit flush in order
                // because Android can suspend the process after the callback.
                if (!pendingLogWrite.IsCompleted && reason != "pause" && reason != "quit") return;
                string previousError = pendingLogWrite.GetAwaiter().GetResult();
                pendingLogWrite = null;
                if (previousError != null)
                {
                    StopLogging(previousError);
                    return;
                }
            }
            if (reason != "start" && reason != "pause" && reason != "quit")
            {
                string path = logPath, report = LatestReport;
                pendingLogWrite = Task.Run(() => AppendReport(path, report));
                return;
            }
#endif
            string error = AppendReport(logPath, LatestReport);
            if (error != null) StopLogging(error);
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
