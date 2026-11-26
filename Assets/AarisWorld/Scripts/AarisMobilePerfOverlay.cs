using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace Aaris.MapReview
{
    // Opt-in Android diagnostics: never changes scene quality, physics, HUD or gameplay.
    // No disk IO, collections or formatted strings in the per-frame sample path.
    [DefaultExecutionOrder(32000)]
    public sealed class AarisMobilePerfOverlay : MonoBehaviour
    {
        private const int SampleCapacity = 256;
        private const float RefreshIntervalSeconds = 1f;
        private const float GestureHoldSeconds = 1.2f;
        private const float GestureReleaseSeconds = 0.6f;
        private const int MaxPendingCsvChars = 65536;
        private const double BytesPerMiB = 1048576.0;
        private const int CaptureBuckets = 4001;
        private const float CaptureWarmupSeconds = 20f;

        private static AarisMobilePerfOverlay instance;
        private readonly float[] recentFramesMs = new float[SampleCapacity];
        private readonly float[] sortedFramesMs = new float[SampleCapacity];
        private readonly StringBuilder pendingCsv = new StringBuilder(2048);

        private int frameCount, nextFrameIndex, stallsOver50, stallsOver100;
        private float maxFrameMs, nextReportAt, gestureStartedAt, gestureReadyAt;
        private int gestureFingers;
        private bool gestureConsumed, overlayVisible, recording, writerFailed;
        private string csvPath, display = "";
        private GUIStyle overlayStyle;
        private ProfilerRecorder mainThreadRecorder, gcAllocRecorder;
        private Task writeTask;
        private FrameTiming[] frameTiming;
        private AarisVehicleWorld vehicleWorld;
        private int[] captureHistogram;
        private bool capturing, captureStarted, skipCaptureFrame;
        private float captureStartsAt, captureEndsAt, capturePausedAt = -1f;
        private int captureFrames, captureStalls, captureLongStalls;
        private int captureFreshAtStart, captureReusedAtStart;
        private int captureCpuCount, captureRenderCount, captureGpuCount;
        private double captureFrameSum, captureCpuSum, captureRenderSum, captureGpuSum;
        private double capturePeakReservedMiB;
        private float captureWorstMs;
        private int captureThermalStart;
        private string captureReport = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState() { instance = null; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
#if UNITY_ANDROID || UNITY_EDITOR || DEVELOPMENT_BUILD
            if (instance != null) return;
            var host = new GameObject("__AarisMobilePerfOverlay");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<AarisMobilePerfOverlay>();
#endif
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            nextReportAt = Time.unscaledTime + RefreshIntervalSeconds;
            display = "Perf capture warming up...";
        }

        private void Update()
        {
            HandleControls();
            if (!overlayVisible && !recording && !capturing)
            {
                if (pendingCsv.Length > 0 || writeTask != null) TryFlushCsv();
                return;
            }

            float ms = Time.unscaledDeltaTime * 1000f;
            float now = Time.unscaledTime;
            if (capturing && now >= captureStartsAt && capturePausedAt < 0f)
            {
                if (!captureStarted)
                {
                    // Start counters after warmup and exclude the interval
                    // that straddled the beginning of the measurement.
                    captureStarted = true;
                    captureThermalStart = AarisAndroidThermalGovernor.ThermalStatus;
                    vehicleWorld = AarisVehicleWorld.ActiveWorld;
                    captureFreshAtStart = vehicleWorld != null ? vehicleWorld.FreshVehicleBuilds : 0;
                    captureReusedAtStart = vehicleWorld != null ? vehicleWorld.ReusedVehicleBuilds : 0;
                }
                else if (skipCaptureFrame) skipCaptureFrame = false;
                else if (ms > 0f && ms < 120000f) RecordCaptureFrame(ms);
            }
            if (ms >= 0f && ms < 120000f)
            {
                recentFramesMs[nextFrameIndex] = ms;
                nextFrameIndex = (nextFrameIndex + 1) % SampleCapacity;
                if (frameCount < SampleCapacity) frameCount++;
                if (ms > maxFrameMs) maxFrameMs = ms;
                if (ms >= 50f) stallsOver50++;
                if (ms >= 100f) stallsOver100++;
            }

            if (Time.unscaledTime < nextReportAt) return;
            nextReportAt = Time.unscaledTime + RefreshIntervalSeconds;
            if (frameCount == 0) return;
            // One 100-slot census per second ONLY while overlay/CSV is on.
            // No native renderer/receiver reads in disabled gameplay.
            AarisOfflineBRDirector.RivalPerfSample ai;
            bool aiReady = AarisOfflineBRDirector.TrySampleRivalPerf(out ai);
            string aiLabel = aiReady
                ? string.Format(CultureInfo.InvariantCulture,
                    "spawn {0} alive {1} dormant {2} virtual {3} rig {4} far {5} phys {6} attackers {7}",
                    ai.spawned, ai.alive, ai.dormant, ai.virtualBodies,
                    ai.detailedRigs, ai.farMeshes, ai.physicalReceivers, ai.attackers)
                : "battle not started";

            Array.Copy(recentFramesMs, sortedFramesMs, frameCount);
            Array.Sort(sortedFramesMs, 0, frameCount);
            float p50 = Percentile(0.50f);
            float p95 = Percentile(0.95f);
            float p99 = Percentile(0.99f);
            float sum = 0f;
            for (int i = 0; i < frameCount; i++) sum += recentFramesMs[i];
            float fps = sum > 0f ? 1000f * frameCount / sum : 0f;
            double cpuMs = mainThreadRecorder.Valid ? mainThreadRecorder.LastValue / 1000000.0 : -1.0;
            long gcBytes = ReadGcAllocatedBytes(gcAllocRecorder);
            double monoMiB = Profiler.GetMonoUsedSizeLong() / BytesPerMiB;
            double unityReservedMiB = Profiler.GetTotalReservedMemoryLong() / BytesPerMiB;
            if (capturing && now >= captureStartsAt && capturePausedAt < 0f)
            {
                capturePeakReservedMiB = Math.Max(capturePeakReservedMiB, unityReservedMiB);
                SampleCaptureTimings();
                if (now >= captureEndsAt) FinishCapture();
            }

            display = string.Format(CultureInfo.InvariantCulture,
                "AARIS PERF  |  F8 / 3 fingers: overlay\n" +
                "FPS avg {0:F1}  |  target {1}\n" +
                "Frame ms p50 {2:F1}  p95 {3:F1}  p99 {4:F1}\n" +
                "Worst {5:F1} ms  |  >=50ms {6}  >=100ms {7}\n" +
                "CPU main last {8} ms  |  GC last {9} B\n" +
                "Unity mono {10:F0} MiB  reserved {11:F0} MiB\n" +
                "AI {13}\n" +
                "CSV {12}  |  F9 / 4 fingers: toggle\n{14}",
                fps, Application.targetFrameRate, p50, p95, p99,
                maxFrameMs, stallsOver50, stallsOver100,
                cpuMs < 0 ? "n/a" : cpuMs.ToString("F1", CultureInfo.InvariantCulture),
                gcBytes < 0 ? "n/a" : gcBytes.ToString(CultureInfo.InvariantCulture),
                monoMiB, unityReservedMiB, writerFailed ? "ERROR" : recording ? "ON" : "OFF",
                aiLabel, CaptureStatus(now));

            if (recording && !writerFailed)
            {
                if (pendingCsv.Length < MaxPendingCsvChars)
                {
                    pendingCsv.Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                        .Append(SceneManager.GetActiveScene().buildIndex).Append(',')
                        .Append(fps.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                        .Append(p50.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                        .Append(p95.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                        .Append(p99.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                        .Append(maxFrameMs.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                        .Append(stallsOver50).Append(',').Append(stallsOver100).Append(',')
                        .Append(cpuMs.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                        .Append(gcBytes).Append(',')
                        .Append(monoMiB.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                        .Append(unityReservedMiB.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                        .Append(aiReady ? ai.spawned : -1).Append(',')
                        .Append(aiReady ? ai.alive : -1).Append(',')
                        .Append(aiReady ? ai.dormant : -1).Append(',')
                        .Append(aiReady ? ai.virtualBodies : -1).Append(',')
                        .Append(aiReady ? ai.detailedRigs : -1).Append(',')
                        .Append(aiReady ? ai.farMeshes : -1).Append(',')
                        .Append(aiReady ? ai.physicalReceivers : -1).Append(',')
                        .Append(aiReady ? ai.attackers : -1).AppendLine();
                }
                else { recording = false; writerFailed = true; }
            }
            stallsOver50 = 0;
            stallsOver100 = 0;
            maxFrameMs = 0f;
            TryFlushCsv();
        }

        private float Percentile(float fraction)
        {
            int index = Mathf.Clamp(Mathf.CeilToInt(frameCount * fraction) - 1, 0, frameCount - 1);
            return sortedFramesMs[index];
        }

        private void StartCapture(int seconds)
        {
            if (captureHistogram == null) captureHistogram = new int[CaptureBuckets];
            else Array.Clear(captureHistogram, 0, captureHistogram.Length);
            captureFrames = captureStalls = captureLongStalls = 0;
            captureCpuCount = captureRenderCount = captureGpuCount = 0;
            captureFrameSum = captureCpuSum = captureRenderSum = captureGpuSum = 0d;
            capturePeakReservedMiB = captureWorstMs = 0f;
            captureStartsAt = Time.unscaledTime + CaptureWarmupSeconds;
            captureEndsAt = captureStartsAt + seconds;
            capturePausedAt = -1f;
            captureStarted = skipCaptureFrame = false;
            captureReport = "";
            capturing = true;
        }

        private void RecordCaptureFrame(float ms)
        {
            // 0.25 ms bins through 1 second plus an overflow bin. This
            // measures the whole run with constant memory at any device FPS.
            int bucket = Mathf.Min(CaptureBuckets - 1, (int)(ms * 4f));
            captureHistogram[bucket]++;
            captureFrames++;
            captureFrameSum += ms;
            captureWorstMs = Mathf.Max(captureWorstMs, ms);
            if (ms > 1000f / 30f) captureStalls++;
            if (ms >= 100f) captureLongStalls++;
        }

        private string CaptureP99()
        {
            int rank = (int)Math.Ceiling(captureFrames * 0.99);
            int cumulative = 0;
            for (int i = 0; i < CaptureBuckets - 1; i++)
            {
                cumulative += captureHistogram[i];
                if (cumulative >= rank)
                    return ((i + 1) * .25f).ToString("F2", CultureInfo.InvariantCulture);
            }
            return ">=1000";
        }

        private void SampleCaptureTimings()
        {
            if (!FrameTimingManager.IsFeatureEnabled()) return;
            if (frameTiming == null) frameTiming = new FrameTiming[1];
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1u, frameTiming) == 0u) return;
            FrameTiming timing = frameTiming[0];
            AccumulateTiming(timing.cpuMainThreadFrameTime, ref captureCpuSum, ref captureCpuCount);
            AccumulateTiming(timing.cpuRenderThreadFrameTime, ref captureRenderSum, ref captureRenderCount);
            AccumulateTiming(timing.gpuFrameTime, ref captureGpuSum, ref captureGpuCount);
        }

        private static void AccumulateTiming(double ms, ref double sum, ref int count)
        {
            if (!(ms > 0d && ms < 120000d)) return;
            sum += ms;
            count++;
        }

        private static string FormatAverage(double sum, int count)
        {
            return count > 0 ? (sum / count).ToString("F2", CultureInfo.InvariantCulture) : "n/a";
        }

        private void FinishCapture()
        {
            capturing = false;
            if (captureFrames == 0) return;
            string vehicleSummary = vehicleWorld == null ? "n/a" :
                string.Format(CultureInfo.InvariantCulture, "{0} built, {1} reused, {2} currently cached",
                    vehicleWorld.FreshVehicleBuilds - captureFreshAtStart,
                    vehicleWorld.ReusedVehicleBuilds - captureReusedAtStart,
                    vehicleWorld.ParkedCacheCount);
            captureReport = string.Format(CultureInfo.InvariantCulture,
                "Aaris {0} | {1} | {2}\n" +
                "{3:F1}s, {4} frames | {5:F1} FPS avg | p99 frame {6} ms | worst {7:F2} ms\n" +
                ">33.3 ms {8} | >=100 ms {9} | Unity reserved sampled peak {10:F0} MiB\n" +
                "Sampled CPU main/render/GPU ms {11}/{12}/{13}\n" +
                "Android thermal status {14} -> {15} (-1 unavailable)\n" +
                "Vehicles {16}",
                Application.version, SystemInfo.graphicsDeviceType, SystemInfo.graphicsDeviceName,
                captureFrameSum / 1000d, captureFrames, captureFrames * 1000d / captureFrameSum,
                CaptureP99(), captureWorstMs, captureStalls, captureLongStalls,
                capturePeakReservedMiB, FormatAverage(captureCpuSum, captureCpuCount),
                FormatAverage(captureRenderSum, captureRenderCount), FormatAverage(captureGpuSum, captureGpuCount),
                captureThermalStart, AarisAndroidThermalGovernor.ThermalStatus, vehicleSummary);
        }

        private string CaptureStatus(float now)
        {
            if (!capturing) return captureReport.Length > 0
                ? "Capture ready to copy" : "Capture includes a 20s warmup";
            bool warmingUp = now < captureStartsAt;
            int remaining = Mathf.Max(0, Mathf.CeilToInt((warmingUp ? captureStartsAt : captureEndsAt) - now));
            return (warmingUp ? "Capture warmup " : "Capture running ") + remaining + "s";
        }

        private void OnApplicationPause(bool paused)
        {
            if (!capturing) return;
            if (paused)
            {
                if (capturePausedAt < 0f) capturePausedAt = Time.unscaledTime;
            }
            else if (capturePausedAt >= 0f)
            {
                float gap = Mathf.Max(0f, Time.unscaledTime - capturePausedAt);
                captureStartsAt += gap;
                captureEndsAt += gap;
                capturePausedAt = -1f;
                skipCaptureFrame = true;
            }
        }

        private void HandleControls()
        {
            if (Input.GetKeyDown(KeyCode.F8)) SetOverlay(!overlayVisible);
            if (Input.GetKeyDown(KeyCode.F9)) SetLogging(!recording);
#if UNITY_ANDROID && !UNITY_EDITOR
            int fingers = Input.touchCount;
            int gesture = fingers == 3 || fingers == 4 ? fingers : 0;
            if (gesture == 0)
            {
                gestureFingers = 0;
                gestureConsumed = false;
                return;
            }
            if (gesture != gestureFingers)
            {
                gestureFingers = gesture;
                gestureStartedAt = Time.unscaledTime;
                gestureConsumed = false;
                return;
            }
            if (gestureConsumed || Time.unscaledTime < gestureReadyAt ||
                Time.unscaledTime - gestureStartedAt < GestureHoldSeconds)
                return;
            gestureConsumed = true;
            gestureReadyAt = Time.unscaledTime + GestureReleaseSeconds;
            if (gesture == 3) SetOverlay(!overlayVisible);
            else SetLogging(!recording);
#endif
        }

        private void SetOverlay(bool value)
        {
            overlayVisible = value;
            UpdateRecorders();
        }

        private void SetLogging(bool value)
        {
            if (value && writerFailed) return;
            if (value && string.IsNullOrEmpty(csvPath))
            {
                csvPath = Path.Combine(Application.persistentDataPath, "AarisDiagnostics",
                    "perf_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
                pendingCsv.AppendLine("utc,scene_build_index,avg_fps,p50_ms,p95_ms,p99_ms,worst_ms,stalls_50ms,stalls_100ms,cpu_main_last_ms,gc_last_bytes,unity_mono_mib,unity_reserved_mib,ai_spawned,ai_alive,ai_dormant,ai_virtual,ai_detailed_rigs,ai_far_meshes,ai_physical_receivers,ai_attackers");
            }
            recording = value;
            UpdateRecorders();
            if (!recording) TryFlushCsv();
        }

        private void UpdateRecorders()
        {
            bool needed = overlayVisible || recording;
            if (needed && !mainThreadRecorder.Valid)
            {
                try { mainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "CPU Main Thread Frame Time"); }
                catch (Exception) { /* Counter may be unsupported on this device. */ }
            }
            if (needed && !gcAllocRecorder.Valid)
            {
                // GC.Alloc is an allocation marker, not the whole-frame byte
                // counter. Never label a marker duration/count as memory.
                try { gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame"); }
                catch (Exception) { /* No fabricated zero when unavailable. */ }
            }
            if (!needed)
            {
                if (mainThreadRecorder.Valid) mainThreadRecorder.Dispose();
                if (gcAllocRecorder.Valid) gcAllocRecorder.Dispose();
            }
        }

        internal static long ReadGcAllocatedBytes(ProfilerRecorder recorder)
        {
            // Release players can omit this counter, and a newly started
            // recorder has no completed frame yet. Both are unavailable, not
            // evidence of zero allocations. Verify units before reading data.
            if (!recorder.Valid || recorder.UnitType != ProfilerMarkerDataUnit.Bytes ||
                recorder.Count == 0) return -1L;
            long value = recorder.LastValue;
            return value >= 0L ? value : -1L;
        }

        private void TryFlushCsv()
        {
            if (writeTask != null)
            {
                if (!writeTask.IsCompleted) return;
                if (writeTask.IsFaulted || writeTask.IsCanceled)
                {
                    writerFailed = true;
                    recording = false;
                    pendingCsv.Length = 0;
                }
                writeTask = null;
            }
            if (writerFailed || pendingCsv.Length == 0 || string.IsNullOrEmpty(csvPath)) return;
            string path = csvPath;
            string chunk = pendingCsv.ToString();
            pendingCsv.Length = 0;
            // Never block Unity's render thread on Android flash writes.
            writeTask = Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, chunk, Encoding.UTF8);
            });
        }

        private void OnGUI()
        {
            if (!overlayVisible) return;
            if (overlayStyle == null)
            {
                overlayStyle = new GUIStyle(GUI.skin.label);
                overlayStyle.normal.textColor = Color.white;
                overlayStyle.fontSize = 13;
                overlayStyle.wordWrap = true;
            }
            float width = Mathf.Min(356f, Screen.width - 16f);
            var outer = new Rect(8f, 8f, width, 224f);
            Color prior = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.82f);
            GUI.DrawTexture(outer, Texture2D.whiteTexture);
            GUI.color = prior;
            GUI.Label(new Rect(14f, 13f, width - 12f, 181f), display, overlayStyle);
            if (capturing)
            {
                if (GUI.Button(new Rect(14f, 193f, 110f, 30f), "Stop capture")) FinishCapture();
            }
            else
            {
                if (GUI.Button(new Rect(14f, 193f, 70f, 30f), "2 min")) StartCapture(120);
                if (GUI.Button(new Rect(90f, 193f, 70f, 30f), "15 min")) StartCapture(900);
                if (captureReport.Length > 0 &&
                    GUI.Button(new Rect(166f, 193f, 110f, 30f), "Copy report"))
                    GUIUtility.systemCopyBuffer = captureReport;
            }
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
            if (mainThreadRecorder.Valid) mainThreadRecorder.Dispose();
            if (gcAllocRecorder.Valid) gcAllocRecorder.Dispose();
            TryFlushCsv();
        }
    }
}
