using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;

namespace Aaris.MapReview
{
    // The APK reviews a fixed terrain. Editor assets stay readable and editable.
    [DisallowMultipleComponent, DefaultExecutionOrder(-300)]
    public sealed class AarisMobileRuntime : MonoBehaviour
    {
        [SerializeField] private Terrain[] terrains;
        [SerializeField] private bool releaseTerrainPaintCpuCopies = true;
        [SerializeField] private int targetFrameRate = 60;
        [SerializeField] private bool tuneForLowRamAndroid = true;
        private readonly float[] frameTimes = new float[180];
        private readonly float[] sortedTimes = new float[180];
        private int frameIndex, frameCount;
        private float sampleAt, unloadAfter;
        private bool lowMemoryPending;
        private AsyncOperation unloading;
        private string logPath;
        private long releasedPaintBytes;
        private string graphicsTier = "unchanged";
        private string lastError = "";
        public static string LatestReport { get; private set; } = "Starting map...";

        private void Awake()
        {
            Application.targetFrameRate = targetFrameRate;
            ConfigureMemoryTier();
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


        // Memory capacity is a coarse starting tier, not a substitute for GPU profiling.
        // Keep the saved 600 m tree draw distance so distant canopy silhouettes remain visible.
        // Only foliage detail and texture streaming are reduced; collision, houses and roads stay intact.
        private void ConfigureMemoryTier()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!tuneForLowRamAndroid) return;
            int ramMB = SystemInfo.systemMemorySize;
            if (ramMB <= 0) { graphicsTier = "ram-unknown"; return; }
            if (ramMB <= 2560)
                ApplyBudget("android-2gb", 96f, 45f, 0.42f, 60f, 18, 10f, 300f);
            else if (ramMB <= 3584)
                ApplyBudget("android-3gb", 160f, 70f, 0.55f, 85f, 32, 7f, 350f);
#endif
        }

        private void ApplyBudget(string name, float mipBudgetMB, float grassDistanceM,
            float grassDensity, float billboardDistanceM, int fullLodTrees,
            float heightmapPixelError, float basemapDistanceM)
        {
            graphicsTier = name;
            // Texture streaming only affects textures imported with mip streaming enabled.
            QualitySettings.streamingMipmapsMemoryBudget =
                Mathf.Min(QualitySettings.streamingMipmapsMemoryBudget, mipBudgetMB);
            Terrain[] candidates = terrains != null && terrains.Length > 0 ? terrains : Terrain.activeTerrains;
            foreach (Terrain terrain in candidates)
            {
                if (terrain == null || terrain.terrainData == null) continue;
                terrain.detailObjectDistance = Mathf.Min(terrain.detailObjectDistance, grassDistanceM);
                terrain.detailObjectDensity = Mathf.Min(terrain.detailObjectDensity, grassDensity);
                terrain.treeBillboardDistance = Mathf.Min(terrain.treeBillboardDistance, billboardDistanceM);
                terrain.treeMaximumFullLODCount = Mathf.Min(terrain.treeMaximumFullLODCount, fullLodTrees);
                terrain.heightmapPixelError = Mathf.Max(terrain.heightmapPixelError, heightmapPixelError);
                terrain.basemapDistance = Mathf.Min(terrain.basemapDistance, basemapDistanceM);
                // Do not lower treeDistance or turn off terrain collision / instancing.
            }
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
#if UNITY_ANDROID && !UNITY_EDITOR
                if (tuneForLowRamAndroid)
                    ApplyBudget("memory-pressure", 64f, 30f, 0.32f, 45f, 12, 12f, 260f);
#endif
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

        private void OnLowMemory() { lowMemoryPending = true; }
        private void OnApplicationPause(bool paused) { if (paused) Record("pause"); }
        private void OnApplicationQuit() { Record("quit"); }
        private void OnDestroy() { Application.lowMemory -= OnLowMemory; Application.logMessageReceived -= OnLog; }
        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            string detail = message + "\n" + stackTrace;
            lastError = detail.Length > 1536 ? detail.Substring(0, 1536) : detail;
        }

        [Serializable]
        private sealed class Sample
        {
            public string reason, utc, version, buildGuid, device, gpu, api, lastError, graphicsTier;
            public int systemRamMB, targetFps, warningCount;
            public float seconds, averageFps, p95FrameMs, mipStreamingBudgetMB;
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
                graphicsTier = graphicsTier, mipStreamingBudgetMB = QualitySettings.streamingMipmapsMemoryBudget,
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
