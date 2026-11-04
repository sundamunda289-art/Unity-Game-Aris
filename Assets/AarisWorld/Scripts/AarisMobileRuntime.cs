using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Aaris.MapReview
{
    // The APK reviews a fixed terrain. Editor assets stay readable and editable.
    [DisallowMultipleComponent, DefaultExecutionOrder(-300)]
    public sealed class AarisMobileRuntime : MonoBehaviour
    {
        [SerializeField] private Terrain[] terrains;
        [SerializeField] private bool releaseTerrainPaintCpuCopies = true;
        [SerializeField] private int targetFrameRate = 60;
        [SerializeField] private bool adaptDetailsToFrameTime = true;
        // Emergency GPU pixel-cost fallback; Android devices up to the 4 GB RAM class only.
        [SerializeField] private bool allowEmergencyRenderScale = true;
        private readonly float[] frameTimes = new float[180];
        private readonly float[] sortedTimes = new float[180];
        private int frameIndex, frameCount;
        private float sampleAt, unloadAfter;
        private bool lowMemoryPending;
        private AsyncOperation unloading;
        private string logPath;
        private long releasedPaintBytes;
        private UniversalRenderPipelineAsset mobilePipeline;
        private float originalStreamingBudget, originalShadowDistance;
        private float originalRenderScale, emergencyRenderScaleFloor;
        private int originalShadowAtlas;
        private bool mobileProfileApplied;
        private string mobileProfile = "baseline";
        // Runtime grass/detail budgets: the far terrain and tree skyline stay visible.
        private Terrain[] profiledTerrains;
        private float[] originalDetailDistances, originalDetailDensities;
        private float[] originalTreeBillboardDistances, originalHeightmapPixelErrors, originalBasemapDistances;
        private int[] originalFullLodTreeCounts;
        private int observedTargetFps;
        private float terrainDetailDistanceCap, terrainDetailDensityCap;
        private float profileShadowDistance, profileTreeBillboardDistance, lowMemoryDetailMultiplier = 1f;
        // Memory warnings keep 3D-tree draw cost capped even if FPS recovers later.
        private float lowMemoryTreeMeshMultiplier = 1f;
        private int profileMaxFullLodTrees;
        // For low-memory GPU pressure, suppress three-LOD static-prop crossfades
        // without changing the authored LOD thresholds, distant silhouette or tree assets.
        [SerializeField] private bool suppressStaticLodCrossFadeUnderPressure = true;
        private LODGroup[] staticLodGroups;
        private LODFadeMode[] originalStaticFadeModes;
        private bool[] originalStaticAnimateCrossFading;
        private bool staticLodCrossFadeSuppressed;
        private int performanceLevel, pressureSamples, recoverySamples;
        private float nextPerformanceChangeAt;
        private string lastError = "";
        public static string LatestReport { get; private set; } = "Starting map...";

        private void Awake()
        {
            Application.targetFrameRate = targetFrameRate;
            observedTargetFps = Application.targetFrameRate;
#if UNITY_ANDROID && !UNITY_EDITOR
            ApplyMobileMemoryProfile();
#endif
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
            // Apply uploads the existing mip chain; it does not resize, recompress or regenerate it.
            // Only the Android read-only review discards CPU paint copies. No GetPixels arrays.
#if UNITY_ANDROID && !UNITY_EDITOR
            if (releaseTerrainPaintCpuCopies && terrains != null)
            {
                foreach (Terrain terrain in terrains)
                {
                    if (terrain == null || terrain.terrainData == null) continue;
                    foreach (Texture2D texture in terrain.terrainData.alphamapTextures)
                    {
                        if (texture == null || !texture.isReadable) continue;
                        long bytes = 0;
                        for (int mip = 0; mip < texture.mipmapCount; mip++)
                            bytes += (long)Mathf.Max(1, texture.width >> mip) * Mathf.Max(1, texture.height >> mip) * 4;
                        try
                        {
                            texture.Apply(false, true);
                            if (!texture.isReadable) releasedPaintBytes += bytes;
                        }
                        catch (UnityException e) { Debug.LogWarning("Terrain CPU paint copy retained: " + e.Message); }
                        // At most one 2048-pixel paint upload per frame during initial preparation.
                        yield return null;
                    }
                }
            }
#endif
            Record("terrain-cpu-copy-pass");
            yield return null;
        }

        private void Update()
        {
            // The in-game FPS switch changes Application.targetFrameRate after Awake.
            // Reset the observation window so intentional 30 FPS pacing is not misread
            // as sustained 60 FPS render pressure.
            int cap = Application.targetFrameRate > 0 ? Application.targetFrameRate : targetFrameRate;
            if (cap != observedTargetFps)
            {
                observedTargetFps = cap;
                ResetFrameObservation();
            }
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
                // Trim streamed mip cache pressure before optional unused-asset cleanup.
                if (mobileProfileApplied)
                {
                    // An already smaller budget must never increase on a low-memory event.
                    float currentBudget = QualitySettings.streamingMipmapsMemoryBudget;
                    QualitySettings.streamingMipmapsMemoryBudget =
                        Mathf.Min(currentBudget, Mathf.Max(48f, currentBudget - 32f));
                    // Keep low-memory reductions in effect even when frame-time quality later recovers.
                    lowMemoryDetailMultiplier = Mathf.Min(lowMemoryDetailMultiplier, 0.75f);
                    lowMemoryTreeMeshMultiplier = Mathf.Min(lowMemoryTreeMeshMultiplier, 0.80f);
                    performanceLevel = Mathf.Max(performanceLevel, 1);
                    ApplyAdaptiveDetailBudget();
                }
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
                sampleAt = Time.unscaledTime + 3;
            }
        }

        // A foreground resume or target-FPS switch must not grade a stale frame
        // window from before the interruption. Preserve the selected quality tier,
        // but require fresh evidence before changing it again.
        private void ResetFrameObservation()
        {
            Array.Clear(frameTimes, 0, frameTimes.Length);
            frameIndex = frameCount = pressureSamples = recoverySamples = 0;
            nextPerformanceChangeAt = Time.unscaledTime + 6f;
        }

        // RAM-class baseline preserves the camera horizon and initial render scale;
        // sustained pressure can later activate the separately guarded render-scale fallback.
        // This is a RAM-class budget, NOT a promise of FPS or a device benchmark.
        private void ApplyMobileMemoryProfile()
        {
            int systemRamMb = SystemInfo.systemMemorySize;
            if (systemRamMb <= 0 || systemRamMb > 4096) return;

            originalStreamingBudget = QualitySettings.streamingMipmapsMemoryBudget;
            mobilePipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (mobilePipeline != null)
            {
                originalShadowDistance = mobilePipeline.shadowDistance;
                originalShadowAtlas = mobilePipeline.mainLightShadowmapResolution;
                originalRenderScale = mobilePipeline.renderScale;
            }

            // Budgets include non-streaming textures. The fixed terrain paint maps already
            // consume a substantial part of this memory: this is not a hard GPU memory cap.
            bool constrained = systemRamMb <= 2304;
            // Retain full authored scale until sustained p95 pressure reaches tier 2.
            // Never increase resolution when the authored URP asset already uses a lower scale.
            emergencyRenderScaleFloor = constrained ? 0.90f : 0.94f;
            QualitySettings.streamingMipmapsMemoryBudget =
                Mathf.Min(originalStreamingBudget, constrained ? 256f : 320f);
            // Global lodBias also moves the final culling cutoff for SpeedTrees
            // and buildings. Keep it authored; source-prefab middle LODs carry
            // the cheaper forest profile without shrinking the distant skyline.
            if (mobilePipeline != null)
            {
                mobilePipeline.shadowDistance = Mathf.Min(originalShadowDistance, constrained ? 90f : 160f);
                mobilePipeline.mainLightShadowmapResolution = Mathf.Min(originalShadowAtlas, 2048);
            }
            // Constrain expensive near-ground details, not distant tree geometry.
            terrainDetailDistanceCap = constrained ? 55f : 80f;
            terrainDetailDensityCap = constrained ? 0.58f : 0.78f;
            profileTreeBillboardDistance = constrained ? 100f : 140f;
            profileMaxFullLodTrees = constrained ? 28 : 44;
            profiledTerrains = terrains != null && terrains.Length > 0 ? terrains : Terrain.activeTerrains;
            originalDetailDistances = new float[profiledTerrains.Length];
            originalDetailDensities = new float[profiledTerrains.Length];
            originalTreeBillboardDistances = new float[profiledTerrains.Length];
            originalFullLodTreeCounts = new int[profiledTerrains.Length];
            originalHeightmapPixelErrors = new float[profiledTerrains.Length];
            originalBasemapDistances = new float[profiledTerrains.Length];
            for (int i = 0; i < profiledTerrains.Length; i++)
            {
                Terrain terrain = profiledTerrains[i];
                if (terrain == null) continue;
                originalDetailDistances[i] = terrain.detailObjectDistance;
                originalDetailDensities[i] = terrain.detailObjectDensity;
                originalTreeBillboardDistances[i] = terrain.treeBillboardDistance;
                originalFullLodTreeCounts[i] = terrain.treeMaximumFullLODCount;
                originalHeightmapPixelErrors[i] = terrain.heightmapPixelError;
                originalBasemapDistances[i] = terrain.basemapDistance;
                terrain.detailObjectDistance = Mathf.Min(originalDetailDistances[i], terrainDetailDistanceCap);
                terrain.detailObjectDensity = Mathf.Min(originalDetailDensities[i], terrainDetailDensityCap);
                // Keep the authored tree draw distance and distant skyline intact:
                // reduce expensive *mesh* trees, not whether far trees exist.
                terrain.treeBillboardDistance = Mathf.Min(originalTreeBillboardDistances[i],
                    profileTreeBillboardDistance);
                terrain.treeMaximumFullLODCount = Mathf.Min(originalFullLodTreeCounts[i],
                    profileMaxFullLodTrees);
                terrain.heightmapPixelError = Mathf.Max(originalHeightmapPixelErrors[i],
                    constrained ? 8f : 6f);
                terrain.basemapDistance = Mathf.Min(originalBasemapDistances[i],
                    constrained ? 400f : 550f);
            }
            profileShadowDistance = mobilePipeline != null ? mobilePipeline.shadowDistance : 0f;
            mobileProfile = constrained ? "ram-2gb" : systemRamMb <= 3584 ? "ram-3gb" : "ram-4gb";
            CaptureStaticLodFadeBaseline();
            mobileProfileApplied = true;
        }

        // Save original LODGroup presentation settings on the active scene, not on
        // source prefabs or native TerrainData. SpeedTree has four LODs and is excluded.
        private void CaptureStaticLodFadeBaseline()
        {
            LODGroup[] groups = UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsSortMode.None);
            int count = 0;
            foreach (LODGroup group in groups)
                if (group != null && group.enabled && group.lodCount == 3 &&
                    group.fadeMode == LODFadeMode.CrossFade) count++;
            staticLodGroups = new LODGroup[count];
            originalStaticFadeModes = new LODFadeMode[count];
            originalStaticAnimateCrossFading = new bool[count];
            int index = 0;
            foreach (LODGroup group in groups)
            {
                if (group == null || !group.enabled || group.lodCount != 3 ||
                    group.fadeMode != LODFadeMode.CrossFade) continue;
                staticLodGroups[index] = group;
                originalStaticFadeModes[index] = group.fadeMode;
                originalStaticAnimateCrossFading[index] = group.animateCrossFading;
                index++;
            }
        }

        // Unity URP advises disabling costly alpha-tested crossfades on low-end
        // devices. Do this only at sustained maximum pressure and reverse on recovery.
        // Exact meshes, LOD boundaries and far-distance visibility stay unchanged.
        private void SetStaticLodCrossFadeSuppressed(bool suppress)
        {
            if (staticLodGroups == null || staticLodCrossFadeSuppressed == suppress) return;
            for (int i = 0; i < staticLodGroups.Length; i++)
            {
                LODGroup group = staticLodGroups[i];
                if (group == null) continue;
                LODFadeMode mode = suppress ? LODFadeMode.None : originalStaticFadeModes[i];
                bool animate = !suppress && originalStaticAnimateCrossFading[i];
                if (group.fadeMode != mode) group.fadeMode = mode;
                if (group.animateCrossFading != animate) group.animateCrossFading = animate;
            }
            staticLodCrossFadeSuppressed = suppress;
        }

        // Grass is reduced first; distant trees, buildings, camera far plane and textures
        // remain visible. A 12-second cooldown and asymmetric thresholds avoid LOD flicker.
        private void TuneForFrameTime(float p95FrameMs)
        {
            if (!mobileProfileApplied || !adaptDetailsToFrameTime ||
                frameCount < frameTimes.Length || Time.unscaledTime < 15f) return;
            float frameBudgetMs = 1000f / Mathf.Clamp(observedTargetFps, 30, 120);
            if (p95FrameMs > frameBudgetMs * 1.30f)
            {
                pressureSamples++;
                recoverySamples = 0;
            }
            else if (p95FrameMs < frameBudgetMs * 1.08f)
            {
                recoverySamples++;
                pressureSamples = 0;
            }
            else
            {
                pressureSamples = 0;
                recoverySamples = 0;
            }
            if (Time.unscaledTime < nextPerformanceChangeAt) return;
            if (pressureSamples >= 2 && performanceLevel < 2)
            {
                performanceLevel++;
                pressureSamples = 0;
                ApplyAdaptiveDetailBudget();
                nextPerformanceChangeAt = Time.unscaledTime + 12f;
            }
            else if (recoverySamples >= 4 && performanceLevel > 0)
            {
                performanceLevel--;
                recoverySamples = 0;
                ApplyAdaptiveDetailBudget();
                nextPerformanceChangeAt = Time.unscaledTime + 12f;
            }
        }

        private void ApplyAdaptiveDetailBudget()
        {
            if (!mobileProfileApplied) return;
            float distanceFactor = performanceLevel == 0 ? 1f : performanceLevel == 1 ? 0.85f : 0.70f;
            float densityFactor = performanceLevel == 0 ? 1f : performanceLevel == 1 ? 0.80f : 0.62f;
            float shadowFactor = performanceLevel == 0 ? 1f : performanceLevel == 1 ? 0.85f : 0.70f;
            // Legacy Terrain tree controls: per Unity, SpeedTree mesh/billboard
            // transitions instead use the source prefab's LODGroup. Do not claim
            // these Terrain limits alone optimize the SpeedTree forest.
            float treeMeshDistanceFactor = performanceLevel == 0 ? 1f : performanceLevel == 1 ? 0.92f : 0.82f;
            float treeMeshCountFactor = performanceLevel == 0 ? 1f : performanceLevel == 1 ? 0.86f : 0.72f;
            if (profiledTerrains != null)
                for (int i = 0; i < profiledTerrains.Length; i++)
                {
                    Terrain terrain = profiledTerrains[i];
                    if (terrain == null) continue;
                    terrain.detailObjectDistance = Mathf.Min(originalDetailDistances[i],
                        terrainDetailDistanceCap * distanceFactor * lowMemoryDetailMultiplier);
                    terrain.detailObjectDensity = Mathf.Min(originalDetailDensities[i],
                        terrainDetailDensityCap * densityFactor * lowMemoryDetailMultiplier);
                    // Tier 0 restores the RAM-class baseline, not a higher authored cost.
                    // Both limits are clamped to original values and retain billboard/far draw.
                    terrain.treeBillboardDistance = Mathf.Min(originalTreeBillboardDistances[i],
                        profileTreeBillboardDistance * treeMeshDistanceFactor * lowMemoryTreeMeshMultiplier);
                    terrain.treeMaximumFullLODCount = Mathf.Min(originalFullLodTreeCounts[i],
                        Mathf.Max(1, Mathf.FloorToInt(profileMaxFullLodTrees * treeMeshCountFactor * lowMemoryTreeMeshMultiplier)));
                }
            SetStaticLodCrossFadeSuppressed(suppressStaticLodCrossFadeUnderPressure &&
                performanceLevel >= 2);
            if (mobilePipeline != null)
            {
                mobilePipeline.shadowDistance = Mathf.Min(originalShadowDistance,
                    profileShadowDistance * shadowFactor);
                // Avoid lowering texture detail, view horizon or house LOD2 visibility.
                // Render scale changes only at maximum sustained pressure and reverses
                // as soon as frame-time recovers; it is a GPU hypothesis, not a benchmark.
                mobilePipeline.renderScale = allowEmergencyRenderScale && performanceLevel >= 2
                    ? Mathf.Min(originalRenderScale, emergencyRenderScaleFloor)
                    : originalRenderScale;
            }
        }

        private void OnLowMemory() { lowMemoryPending = true; }
        private void OnApplicationPause(bool paused)
        {
            if (paused) Record("pause");
            else
            {
                ResetFrameObservation();
                Record("resume");
            }
        }
        private void OnApplicationFocus(bool focused)
        {
            if (focused) ResetFrameObservation();
        }
        private void OnApplicationQuit() { Record("quit"); }
        private void OnDestroy()
        {
            Application.lowMemory -= OnLowMemory;
            Application.logMessageReceived -= OnLog;
            // Runtime-only changes must not leak into the next scene or Editor session.
            if (!mobileProfileApplied) return;
            SetStaticLodCrossFadeSuppressed(false);
            QualitySettings.streamingMipmapsMemoryBudget = originalStreamingBudget;
            if (mobilePipeline != null)
            {
                mobilePipeline.shadowDistance = originalShadowDistance;
                mobilePipeline.mainLightShadowmapResolution = originalShadowAtlas;
                mobilePipeline.renderScale = originalRenderScale;
            }
            if (profiledTerrains != null)
                for (int i = 0; i < profiledTerrains.Length; i++)
                {
                    Terrain terrain = profiledTerrains[i];
                    if (terrain == null) continue;
                    terrain.detailObjectDistance = originalDetailDistances[i];
                    terrain.detailObjectDensity = originalDetailDensities[i];
                    terrain.treeBillboardDistance = originalTreeBillboardDistances[i];
                    terrain.treeMaximumFullLODCount = originalFullLodTreeCounts[i];
                    terrain.heightmapPixelError = originalHeightmapPixelErrors[i];
                    terrain.basemapDistance = originalBasemapDistances[i];
                }
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
            public string reason, utc, version, buildGuid, device, gpu, api, lastError;
            public int systemRamMB, targetFps, warningCount, shadowAtlasResolution, performanceLevel;
            public bool emergencyRenderScaleActive;
            public bool staticLodCrossFadeSuppressed;
            public int staticLodCrossFadeGroupCount;
            public string mobileQualityProfile;
            public float streamingBudgetMb, lodBias, shadowDistanceM, terrainDetailDistanceCapM, terrainDetailDensityCap;
            public float renderScale;
            public float effectiveDetailDistanceM, effectiveDetailDensity, lowMemoryDetailMultiplier, lowMemoryTreeMeshMultiplier;
            public float effectiveTreeBillboardDistanceM, effectiveHeightmapPixelError, effectiveBasemapDistanceM;
            public int effectiveMaxFullLodTrees;
            public float seconds, averageFps, p95FrameMs;
            public long unityAllocatedBytes, unityReservedBytes, managedBytes, androidPssKB;
            public ulong textureBytes, desiredTextureBytes;
            public long terrainCpuPaintBytesReleased;
        }
        private int warningCount;
        private void Record(string reason)
        {
            if (reason == "low-memory") warningCount++;
            float total = 0;
            for (int i = 0; i < frameCount; i++) { total += frameTimes[i]; sortedTimes[i] = frameTimes[i]; }
            Array.Sort(sortedTimes, 0, frameCount);
            var s = new Sample {
                reason = reason, utc = DateTime.UtcNow.ToString("O"), version = Application.version, buildGuid = Application.buildGUID,
                device = SystemInfo.deviceModel, gpu = SystemInfo.graphicsDeviceName, api = SystemInfo.graphicsDeviceType.ToString(),
                systemRamMB = SystemInfo.systemMemorySize, targetFps = Application.targetFrameRate,
                mobileQualityProfile = mobileProfile,
                terrainDetailDistanceCapM = terrainDetailDistanceCap, terrainDetailDensityCap = terrainDetailDensityCap,
                streamingBudgetMb = QualitySettings.streamingMipmapsMemoryBudget, lodBias = QualitySettings.lodBias,
                shadowDistanceM = mobilePipeline != null ? mobilePipeline.shadowDistance : QualitySettings.shadowDistance,
                shadowAtlasResolution = mobilePipeline != null ? mobilePipeline.mainLightShadowmapResolution : 0,
                seconds = Time.unscaledTime, averageFps = total > 0 ? frameCount / total : 0,
                p95FrameMs = frameCount > 0 ? sortedTimes[Mathf.Clamp(Mathf.CeilToInt(frameCount * .95f) - 1, 0, frameCount - 1)] * 1000 : 0,
                unityAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong(), unityReservedBytes = Profiler.GetTotalReservedMemoryLong(),
                managedBytes = GC.GetTotalMemory(false), textureBytes = Texture.currentTextureMemory,
                desiredTextureBytes = Texture.desiredTextureMemory, terrainCpuPaintBytesReleased = releasedPaintBytes,
                androidPssKB = -1, warningCount = warningCount, lastError = lastError
            };
#if UNITY_ANDROID && !UNITY_EDITOR
            try { using (var debug = new AndroidJavaClass("android.os.Debug")) s.androidPssKB = debug.CallStatic<long>("getPss"); }
            catch (Exception e) { Debug.LogWarning("Android PSS unavailable: " + e.GetType().Name); }
#endif
            if (reason == "sample") TuneForFrameTime(s.p95FrameMs);
            s.performanceLevel = performanceLevel;
            s.staticLodCrossFadeSuppressed = staticLodCrossFadeSuppressed;
            s.staticLodCrossFadeGroupCount = staticLodGroups != null ? staticLodGroups.Length : 0;
            s.lowMemoryDetailMultiplier = lowMemoryDetailMultiplier;
            s.lowMemoryTreeMeshMultiplier = lowMemoryTreeMeshMultiplier;
            if (profiledTerrains != null)
                foreach (Terrain terrain in profiledTerrains)
                    if (terrain != null)
                    {
                        s.effectiveDetailDistanceM = terrain.detailObjectDistance;
                        s.effectiveDetailDensity = terrain.detailObjectDensity;
                        s.effectiveTreeBillboardDistanceM = terrain.treeBillboardDistance;
                        s.effectiveMaxFullLodTrees = terrain.treeMaximumFullLODCount;
                        s.effectiveHeightmapPixelError = terrain.heightmapPixelError;
                        s.effectiveBasemapDistanceM = terrain.basemapDistance;
                        break;
                    }
            s.shadowDistanceM = mobilePipeline != null ? mobilePipeline.shadowDistance : QualitySettings.shadowDistance;
            s.renderScale = mobilePipeline != null ? mobilePipeline.renderScale : 1f;
            s.emergencyRenderScaleActive = mobileProfileApplied && mobilePipeline != null &&
                mobilePipeline.renderScale < originalRenderScale - 0.001f;
            LatestReport = JsonUtility.ToJson(s);
            if (logPath == null) return;
            try
            {
                if (new FileInfo(logPath).Length > 256 * 1024) File.WriteAllText(logPath, "");
                File.AppendAllText(logPath, LatestReport + "\n");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            { Debug.LogWarning("Aaris diagnostics write stopped: " + e.Message); logPath = null; }
        }
    }
}
