#!/usr/bin/env python3
"""Android diagnostic frame pacing source regressions (not a device FPS test)."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CODE = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text()
GATE = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileStreamingFrameGate.cs").read_text()


class DiagnosticFramePacing(unittest.TestCase):
    def test_start_snapshot_is_off_first_android_scene_frame(self):
        awake = CODE.split("private void Awake()", 1)[1].split("private IEnumerator Start()", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR\n            startupReportPending = true;\n#else\n            Record(\"start\");\n#endif", awake)
        update = CODE.split("private void Update()", 1)[1].split("private bool CanRecordPeriodicDiagnostics()", 1)[0]
        self.assertIn("Time.unscaledTime - startupTime >= 0.5f", update)
        self.assertIn("CanRecordPeriodicDiagnostics()", update)
        self.assertLess(update.index("startupReportPending = false;"), update.index('Record("start");'))

    def test_stall_does_not_serialize_on_saturated_frame(self):
        update = CODE.split("private void Update()", 1)[1].split("private bool CanRecordPeriodicDiagnostics()", 1)[0]
        hot = update.split("if (dt >= 0.75f", 1)[1].split("#endif", 1)[0]
        self.assertIn("nextLongFrameReportAt = Time.unscaledTime + 5f;", hot)
        self.assertIn("frameStallReportPending = true;", hot)
        self.assertIn("frameStallObservedFrame = Time.frameCount;", hot)
        self.assertNotIn("Record(", hot)
        self.assertIn("Time.frameCount != frameStallObservedFrame", update)
        self.assertIn("frameStallReportPending = false;", update)
        self.assertIn('Record("frame-stall");', update)
        self.assertLess(update.index("frameStallReportPending = false;"),
                        update.index('Record("frame-stall");'))

    def test_no_double_snapshot_or_streamer_competition(self):
        policy = CODE.split("private bool CanRecordPeriodicDiagnostics()", 1)[1].split("private void OnLowMemory()", 1)[0]
        self.assertIn("Time.frameCount != lastDiagnosticFrame", policy)
        self.assertIn("AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame()", policy)
        self.assertIn("private bool frameStallReportPending;", CODE)
        self.assertIn("private bool startupReportPending;", CODE)
        self.assertIn("claimedFrame = frame;", GATE)
        self.assertIn("if (IsEssentialRigFrame() || IsAssetCleanupPending()) return false;", GATE)

    def test_critical_memory_cleanup_and_startup_telemetry_stay(self):
        self.assertIn("Resources.UnloadUnusedAssets()", CODE)
        self.assertIn('Record("low-memory");', CODE)
        self.assertIn('Record("startup");', CODE)
        self.assertIn('Record("frame-stall");', CODE)


    def test_android_terrain_audit_uses_distinct_admitted_frames(self):
        start = CODE.split("private IEnumerator Start()", 1)[1].split(
            "private IEnumerator CollectPreviousProcessExitAfterStartup()", 1)[0]
        self.assertIn("terrainAuditPending = true;", start)
        self.assertIn("#else\n            // Editor terrain verification remains synchronous", start)
        android_start = start.split("#if UNITY_ANDROID && !UNITY_EDITOR", 2)[-1]
        self.assertNotIn('Record("terrain-render-audit");', android_start.split("#else", 1)[0])
        update = CODE.split("private void Update()", 1)[1].split(
            "private bool CanRecordPeriodicDiagnostics()", 1)[0]
        self.assertIn("Time.unscaledTime - startupTime >= 2f", update)
        audit = update.split("if (terrainAuditPending &&", 1)[1].split(
            "if (terrainAuditReportPending &&", 1)[0]
        self.assertIn("CanRecordPeriodicDiagnostics()", audit)
        self.assertIn("lastDiagnosticFrame = Time.frameCount;", audit)
        self.assertIn("AuditNativeTerrain();", audit)
        self.assertNotIn("Record(", audit)
        report = update.split("if (terrainAuditReportPending &&", 1)[1].split(
            "if (previousProcessExitReportPending &&", 1)[0]
        self.assertIn("CanRecordPeriodicDiagnostics()", report)
        self.assertIn('Record("terrain-render-audit");', report)
        self.assertLess(update.index('Record("frame-stall");'),
                        update.index("if (terrainAuditPending &&"))

    def test_previous_process_exit_snapshot_retries_without_native_stall(self):
        collect = CODE.split("private IEnumerator CollectPreviousProcessExitAfterStartup()", 1)[1].split(
            "private void ConfigureOceanShadowBudget()", 1)[0]
        self.assertIn("previousExitReady = true;", collect)
        self.assertIn("previousProcessExitReportPending = true;", collect)
        self.assertNotIn('Record("previous-process-exit");', collect)
        update = CODE.split("private void Update()", 1)[1].split(
            "private bool CanRecordPeriodicDiagnostics()", 1)[0]
        report = update.split("if (previousProcessExitReportPending &&", 1)[1].split(
            "if (startupReportPending &&", 1)[0]
        self.assertIn("CanRecordPeriodicDiagnostics()", report)
        self.assertIn('Record("previous-process-exit");', report)
        self.assertIn("AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame()", CODE)

if __name__ == "__main__":
    unittest.main()
