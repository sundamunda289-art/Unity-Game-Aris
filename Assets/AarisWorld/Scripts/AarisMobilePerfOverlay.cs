using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Unity.Profiling;
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
            if (!overlayVisible && !recording)
            {
                if (pendingCsv.Length > 0 || writeTask != null) TryFlushCsv();
                return;
            }

            float ms = Time.unscaledDeltaTime * 1000f;
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
            long gcBytes = gcAllocRecorder.Valid ? gcAllocRecorder.LastValue : -1L;
            double monoMiB = Profiler.GetMonoUsedSizeLong() / BytesPerMiB;
            double unityReservedMiB = Profiler.GetTotalReservedMemoryLong() / BytesPerMiB;

            display = string.Format(CultureInfo.InvariantCulture,
                "AARIS PERF  |  F8 / 3 fingers: overlay\n" +
                "FPS avg {0:F1}  |  target {1}\n" +
                "Frame ms p50 {2:F1}  p95 {3:F1}  p99 {4:F1}\n" +
                "Worst {5:F1} ms  |  >=50ms {6}  >=100ms {7}\n" +
                "CPU main last {8} ms  |  GC last {9} B\n" +
                "Unity mono {10:F0} MiB  reserved {11:F0} MiB\n" +
                "AI {13}\n" +
                "CSV {12}  |  F9 / 4 fingers: toggle",
                fps, Application.targetFrameRate, p50, p95, p99,
                maxFrameMs, stallsOver50, stallsOver100,
                cpuMs < 0 ? "n/a" : cpuMs.ToString("F1", CultureInfo.InvariantCulture),
                gcBytes < 0 ? "n/a" : gcBytes.ToString(CultureInfo.InvariantCulture),
                monoMiB, unityReservedMiB, writerFailed ? "ERROR" : recording ? "ON" : "OFF",
                aiLabel);

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
                try { gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc"); }
                catch (Exception) { /* No fabricated zero when unavailable. */ }
            }
            if (!needed)
            {
                if (mainThreadRecorder.Valid) mainThreadRecorder.Dispose();
                if (gcAllocRecorder.Valid) gcAllocRecorder.Dispose();
            }
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
            var outer = new Rect(8f, 8f, width, 198f);
            Color prior = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.82f);
            GUI.DrawTexture(outer, Texture2D.whiteTexture);
            GUI.color = prior;
            GUI.Label(new Rect(14f, 13f, width - 12f, 192f), display, overlayStyle);
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
