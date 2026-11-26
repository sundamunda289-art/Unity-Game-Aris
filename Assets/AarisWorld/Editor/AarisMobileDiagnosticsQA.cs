#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEditor;
using UnityEngine;

namespace Aaris.MapReview.Editor
{
    // Checks the real native counter selected by the shipped overlay.
    // Does not enter Play Mode, save assets or change rendering settings.
    public static class AarisMobileDiagnosticsQA
    {
        [MenuItem("Aaris/CI/Verify mobile diagnostics counter units")]
        public static void Verify()
        {
            const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic;
            string output = Path.Combine(Path.GetTempPath(), "aaris-mobile-diagnostics-qa.txt");
            var evidence = new StringBuilder("Unity " + Application.unityVersion + "\n");
            GameObject host = null;
            AarisMobilePerfOverlay overlay = null;
            ProfilerRecorder oldMarker = default;
            ProfilerRecorder timing = default;
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                    throw new InvalidOperationException("EDITOR_BUSY");
                host = new GameObject("Temporary mobile diagnostics QA");
                host.hideFlags = HideFlags.DontSave;
                host.SetActive(false);
                overlay = host.AddComponent<AarisMobilePerfOverlay>();
                Type type = typeof(AarisMobilePerfOverlay);
                type.GetField("overlayVisible", instanceFlags).SetValue(overlay, true);
                type.GetMethod("UpdateRecorders", instanceFlags).Invoke(overlay, null);
                var bytes = (ProfilerRecorder)type.GetField("gcAllocRecorder", instanceFlags).GetValue(overlay);
                Require(bytes.Valid, "native whole-frame allocation counter exists");
                Require(bytes.UnitType == ProfilerMarkerDataUnit.Bytes, "native allocation counter reports Bytes");
                evidence.AppendLine("GC_ALLOCATED_IN_FRAME_UNIT=" + bytes.UnitType);

                MethodInfo read = type.GetMethod("ReadGcAllocatedBytes", staticFlags);
                Require((long)read.Invoke(null, new object[] { default(ProfilerRecorder) }) == -1L,
                    "unavailable counter is not reported as zero bytes");
                evidence.AppendLine("INVALID_RECORDER=unavailable");
                if (bytes.Count == 0)
                {
                    Require((long)read.Invoke(null, new object[] { bytes }) == -1L,
                        "unsampled counter is not reported as zero bytes");
                    evidence.AppendLine("UNSAMPLED_COUNTER=unavailable");
                }

                timing = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
                Require(timing.Valid && timing.UnitType != ProfilerMarkerDataUnit.Bytes,
                    "native timing counter has non-memory units");
                Require((long)read.Invoke(null, new object[] { timing }) == -1L,
                    "timing data cannot be labelled GC bytes");
                evidence.AppendLine("TIMING_COUNTER_REJECTED=" + timing.UnitType);

                oldMarker = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc");
                evidence.AppendLine("PREVIOUS_GC_ALLOC_MARKER_UNIT=" +
                    (oldMarker.Valid ? oldMarker.UnitType.ToString() : "unavailable"));
                evidence.AppendLine("RESULT=PASS");
                Debug.Log("AARIS_MOBILE_DIAGNOSTICS_QA_PASS native GC bytes unit verified");
            }
            catch (Exception e)
            {
                evidence.AppendLine("RESULT=FAIL " + e.GetType().Name + ": " + e.Message);
                throw;
            }
            finally
            {
                if (oldMarker.Valid) oldMarker.Dispose();
                if (timing.Valid) timing.Dispose();
                // The inactive temporary component never runs Awake/Update;
                // release its native recorders explicitly before disposal.
                if (overlay != null)
                {
                    Type type = typeof(AarisMobilePerfOverlay);
                    type.GetField("overlayVisible", instanceFlags).SetValue(overlay, false);
                    type.GetMethod("UpdateRecorders", instanceFlags).Invoke(overlay, null);
                }
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
                File.WriteAllText(output, evidence.ToString());
            }
        }

        private static void Require(bool passed, string requirement)
        {
            if (!passed) throw new InvalidOperationException(requirement);
        }
    }
}
#endif
