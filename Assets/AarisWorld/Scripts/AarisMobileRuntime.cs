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
        private readonly float[] frameTimes = new float[180];
        private readonly float[] sortedTimes = new float[180];
        private int frameIndex, frameCount;
        private float sampleAt, unloadAfter;
        private bool lowMemoryPending;
        private AsyncOperation unloading;
        private string logPath;
        private long releasedPaintBytes;
        private UniversalRenderPipelineAsset mobilePipeline;
        private float originalStreamingBudget, originalLodBias, originalShadowDistance;
        private int originalShadowAtlas;
        private bool mobileProfileApplied;
        private string mobileProfile = "baseline";
        // Runtime grass/detail budgets: the far terrain and tree skyline stay visible.
        private Terrain[] profiledTerrains;
        private float[] originalDetailDistances, originalDetailDensities;
        private float terrainDetailDistanceCap, terrainDetailDensityCap;
        private string lastError = "";
        public static string LatestReport { get; private set; } = "Starting map...";

        private void Awake()
        {
            Application.targetFrameRate = targetFrameRate;
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
                    if (profiledTerrains != null)
                        foreach (Terrain terrain in profiledTerrains)
                            if (terrain != null)
                                terrain.detailObjectDistance = Mathf.Min(terrain.detailObjectDistance,
                                    terrainDetailDistanceCap * 0.75f);
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

        // No changes to camera far clipping or render scale: the distant skyline remains visible.
        // This is a RAM-class budget, NOT a promise of FPS or a device benchmark.
        private void ApplyMobileMemoryProfile()
        {
            int systemRamMb = SystemInfo.systemMemorySize;
            if (systemRamMb <= 0 || systemRamMb > 3584) return;

            originalStreamingBudget = QualitySettings.streamingMipmapsMemoryBudget;
            originalLodBias = QualitySettings.lodBias;
            mobilePipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (mobilePipeline != null)
            {
                originalShadowDistance = mobilePipeline.shadowDistance;
                originalShadowAtlas = mobilePipeline.mainLightShadowmapResolution;
            }

            // Budgets include non-streaming textures. The fixed terrain paint maps already
            // consume a substantial part of this memory: this is not a hard GPU memory cap.
            bool constrained = systemRamMb <= 2304;
            QualitySettings.streamingMipmapsMemoryBudget =
                Mathf.Min(originalStreamingBudget, constrained ? 256f : 320f);
            QualitySettings.lodBias = Mathf.Min(originalLodBias, constrained ? 0.90f : 0.98f);
            if (mobilePipeline != null)
            {
                mobilePipeline.shadowDistance = Mathf.Min(originalShadowDistance, constrained ? 90f : 160f);
                mobilePipeline.mainLightShadowmapResolution = Mathf.Min(originalShadowAtlas, 2048);
            }
            // Constrain expensive near-ground details, not distant tree geometry.
            terrainDetailDistanceCap = constrained ? 55f : 80f;
            terrainDetailDensityCap = constrained ? 0.58f : 0.78f;
            profiledTerrains = terrains != null && terrains.Length > 0 ? terrains : Terrain.activeTerrains;
            originalDetailDistances = new float[profiledTerrains.Length];
            originalDetailDensities = new float[profiledTerrains.Length];
            for (int i = 0; i < profiledTerrains.Length; i++)
            {
                Terrain terrain = profiledTerrains[i];
                if (terrain == null) continue;
                originalDetailDistances[i] = terrain.detailObjectDistance;
                originalDetailDensities[i] = terrain.detailObjectDensity;
                terrain.detailObjectDistance = Mathf.Min(originalDetailDistances[i], terrainDetailDistanceCap);
                terrain.detailObjectDensity = Mathf.Min(originalDetailDensities[i], terrainDetailDensityCap);
            }
            mobileProfile = constrained ? "ram-2gb" : "ram-3gb";
            mobileProfileApplied = true;
        }

        private void OnLowMemory() { lowMemoryPending = true; }
        private void OnApplicationPause(bool paused) { if (paused) Record("pause"); }
        private void OnApplicationQuit() { Record("quit"); }
        private void OnDestroy()
        {
            Application.lowMemory -= OnLowMemory;
            Application.logMessageReceived -= OnLog;
            // Runtime-only changes must not leak into the next scene or Editor session.
            if (!mobileProfileApplied) return;
            QualitySettings.streamingMipmapsMemoryBudget = originalStreamingBudget;
            QualitySettings.lodBias = originalLodBias;
            if (mobilePipeline != null)
            {
                mobilePipeline.shadowDistance = originalShadowDistance;
                mobilePipeline.mainLightShadowmapResolution = originalShadowAtlas;
            }
            if (profiledTerrains != null)
                for (int i = 0; i < profiledTerrains.Length; i++)
                {
                    Terrain terrain = profiledTerrains[i];
                    if (terrain == null) continue;
                    terrain.detailObjectDistance = originalDetailDistances[i];
                    terrain.detailObjectDensity = originalDetailDensities[i];
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
            public int systemRamMB, targetFps, warningCount, shadowAtlasResolution;
            public string mobileQualityProfile;
            public float streamingBudgetMb, lodBias, shadowDistanceM, terrainDetailDistanceCapM, terrainDetailDensityCap;
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
