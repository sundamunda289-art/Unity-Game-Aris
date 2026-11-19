#!/usr/bin/env python3
"""Source and deterministic scheduling checks for non-urgent Android reports.

No Unity binary or Android FPS claims are made by these checks.
"""
from pathlib import Path
import unittest

SRC = (Path(__file__).resolve().parents[2] /
       "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text()


def simulate_periodic_reports(frame_events, startup_due_frames, sample_due_frames):
    """Mirrors priority: urgent first, then startup, then 15-second sample.

    Due reports stay pending when an earlier report used the frame. Nothing
    simulates the native cost of JsonUtility, Unity Profiler or flash storage.
    """
    results = []
    startup_pending = 0
    sample_pending = 0
    for frame in range(1, len(frame_events) + 1):
        if frame in startup_due_frames:
            startup_pending += startup_due_frames[frame]
        if frame in sample_due_frames:
            sample_pending += sample_due_frames[frame]
        urgent = frame_events[frame - 1]
        results.extend((frame, reason) for reason in urgent)
        if urgent:
            continue
        if startup_pending:
            startup_pending -= 1
            results.append((frame, "startup"))
        elif sample_pending:
            sample_pending -= 1
            results.append((frame, "sample"))
    return results, startup_pending, sample_pending


class AndroidDiagnosticFramePacing(unittest.TestCase):
    def test_android_only_periodic_frame_admission(self):
        self.assertIn("private int lastDiagnosticFrame = -1;", SRC)
        self.assertIn("private bool CanRecordPeriodicDiagnostics()", SRC)
        gate = SRC.split("private bool CanRecordPeriodicDiagnostics()", 1)[1].split(
            "private void OnLowMemory()", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", gate)
        self.assertIn("Time.frameCount != lastDiagnosticFrame &&", gate)
        self.assertIn("Time.frameCount != lowMemoryReportObservedFrame &&", gate)
        self.assertIn("Time.frameCount != assetUnloadCompletionObservedFrame &&", gate)
        self.assertIn(
            "AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame();", gate)
        self.assertIn("#else\n            return true;", gate)
        record = SRC.split("private void Record(string reason)", 1)[1].split(
            "private static string InitializeLogFile", 1)[0]
        self.assertIn("lastDiagnosticFrame = Time.frameCount;", record)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", record)

    def test_no_change_to_urgent_stall_and_low_memory_recording(self):
        update = SRC.split("private void Update()", 1)[1].split(
            "private bool CanRecordPeriodicDiagnostics()", 1)[0]
        self.assertIn('Record("frame-stall");', update)
        self.assertIn('Record("low-memory");', update)
        self.assertIn('Record("asset-unload-complete");', update)
        self.assertIn("dt >= 0.75f", update)
        self.assertIn("unloading = Resources.UnloadUnusedAssets();", update)
        self.assertIn('if (startupSampleIndex < 3)', update)
        self.assertIn('if (seconds >= threshold && CanRecordPeriodicDiagnostics())', update)
        self.assertIn('if (Time.unscaledTime >= sampleAt && CanRecordPeriodicDiagnostics())', update)
        self.assertLess(update.index('Record("frame-stall");'),
                        update.index('Record("startup");'))
        self.assertLess(update.index('Record("low-memory");'),
                        update.index('Record("startup");'))
        self.assertLess(update.index('Record("startup");'),
                        update.index('Record("sample");'))

    def test_startup_report_claims_only_after_due_threshold(self):
        update = SRC.split("private void Update()", 1)[1].split(
            "private bool CanRecordPeriodicDiagnostics()", 1)[0]
        startup = update.split("if (startupSampleIndex < 3)", 1)[1].split(
            "if (Time.unscaledTime >= sampleAt", 1)[0]
        self.assertIn(
            "if (seconds >= threshold && CanRecordPeriodicDiagnostics())", startup)
        self.assertLess(startup.index("seconds >= threshold"),
                        startup.index("CanRecordPeriodicDiagnostics()"))
        self.assertLess(startup.index("CanRecordPeriodicDiagnostics()"),
                        startup.index('Record("startup");'))
        self.assertNotIn("TryClaimNonessentialDiagnosticFrame()", startup)

    def test_low_memory_report_waits_until_after_native_cleanup_start(self):
        update = SRC.split("private void Update()", 1)[1].split(
            "private bool CanRecordPeriodicDiagnostics()", 1)[0]
        cleanup = update.split("if (lowMemoryPending)", 1)[1].split(
            "// Avoid two diagnostic snapshots in the same frame.", 1)[0]
        self.assertIn("AarisOfflineFragGrenade.ReleaseInactivePooledGrenades();", cleanup)
        self.assertIn("unloading = Resources.UnloadUnusedAssets();", cleanup)
        self.assertIn("lowMemoryReportPending = true;", cleanup)
        self.assertIn("lowMemoryReportObservedFrame = Time.frameCount;", cleanup)
        self.assertLess(cleanup.index("unloading = Resources.UnloadUnusedAssets();"),
                        cleanup.index("lowMemoryReportPending = true;"))
        report = update.split("if (lowMemoryReportPending &&", 1)[1].split(
            "// The regular 15-second ring", 1)[0]
        self.assertIn("Time.frameCount != lowMemoryReportObservedFrame", report)
        self.assertIn("CanRecordPeriodicDiagnostics()", report)
        self.assertIn("lowMemoryReportPending = false;", report)
        self.assertIn('Record("low-memory");', report)
        # Android reports later; Editor retains the original immediate log.
        self.assertIn("#else", cleanup)
        self.assertIn('Record("low-memory");', cleanup)
        self.assertIn("#endif", cleanup)
        self.assertNotIn("GC.Collect()", cleanup)
        self.assertNotIn("WaitForCompletion", cleanup)

    def test_native_unload_completion_never_steals_its_own_report_frame(self):
        update = SRC.split("private void Update()", 1)[1].split(
            "private bool CanRecordPeriodicDiagnostics()", 1)[0]
        self.assertIn("assetUnloadCompletionObservedFrame = Time.frameCount;", update)
        self.assertIn("assetUnloadCompletionReportPending = true;", update)
        gate = SRC.split("private bool CanRecordPeriodicDiagnostics()", 1)[1].split(
            "private void OnLowMemory()", 1)[0]
        self.assertIn("Time.frameCount != assetUnloadCompletionObservedFrame", gate)
        self.assertIn("Time.frameCount != lowMemoryReportObservedFrame", gate)

    def test_bursts_cannot_consume_two_periodic_snapshots_same_frame(self):
        records, waiting_startup, waiting_sample = simulate_periodic_reports(
            [[], ["frame-stall"], [], ["low-memory"], [],
             ["asset-unload-complete"], [], [], []],
            {2: 1, 4: 1, 6: 1}, {4: 1})
        self.assertEqual(waiting_startup, 0)
        self.assertEqual(waiting_sample, 0)
        self.assertEqual([frame for frame, _ in records if frame in (2, 4, 6)],
                         [2, 4, 6])
        self.assertEqual([reason for _, reason in records].count("startup"), 3)
        self.assertEqual([reason for _, reason in records].count("sample"), 1)
        for frame in range(1, 10):
            periodic = [reason for f, reason in records if f == frame
                        and reason in ("startup", "sample")]
            self.assertLessEqual(len(periodic), 1)
            if any(reason not in ("startup", "sample")
                   for f, reason in records if f == frame):
                self.assertEqual(periodic, [])

    def test_periodic_deadlines_stay_pending_until_uncontended_frame(self):
        records, startup_left, sample_left = simulate_periodic_reports(
            [["frame-stall"], ["low-memory"], [], [], []],
            {1: 1}, {1: 1})
        self.assertEqual(startup_left, 0)
        self.assertEqual(sample_left, 0)
        self.assertEqual(records[-2:], [(3, "startup"), (4, "sample")])

    def test_graphics_assets_and_essential_runtime_unchanged(self):
        self.assertIn("private void ConfigureTerrainInstanceRenderer()", SRC)
        self.assertIn("private void ConfigureLowMemoryVegetation()", SRC)
        self.assertIn("AarisMobileStreamingFrameGate.DeferOptionalWorkDuringAssetCleanup(unloading);", SRC)
        self.assertNotIn("QualitySettings.globalTextureMipmapLimit =", SRC)


if __name__ == "__main__":
    unittest.main(verbosity=2)
