"""Source-level gate for Android-only native async upload budget and restoration.

This is deliberately not a substitute for a Unity player build or an Android trace.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text()


class TestAndroidAsyncUploadGovernor(unittest.TestCase):
    def test_uses_native_upload_timeslice_only(self):
        self.assertIn("QualitySettings.asyncUploadTimeSlice = 1;", SOURCE)
        self.assertIn("QualitySettings.asyncUploadTimeSlice = originalAsyncUploadTimeSlice;", SOURCE)
        self.assertNotIn("QualitySettings.asyncUploadBufferSize =", SOURCE)
        self.assertNotIn("QualitySettings.asyncUploadPersistentBuffer =", SOURCE)

    def test_requires_sustained_missed_frames_and_actual_imports(self):
        self.assertIn("bool sustainedPacingMiss = snapshot.p75FrameMs >", SOURCE)
        self.assertIn("targetFrameMs * 1.20f;", SOURCE)
        self.assertIn("bool pressure = importsObserved &&", SOURCE)
        self.assertIn("snapshot.p95FrameMs > targetFrameMs * 1.4f", SOURCE)
        self.assertIn("(snapshot.framesOver50Ms >= 3 || sustainedPacingMiss)", SOURCE)
        self.assertIn("asyncUploadRecoveryWindows >= 2", SOURCE)
        self.assertIn("current != appliedAsyncUploadTimeSlice", SOURCE)

    def test_budget_relative_sustained_misses_on_30_45_and_60_fps(self):
        # Logic mirror only: production still evaluates once per 15-s sample.
        def pressured(fps, p75_ms, p95_ms, over50, imports):
            budget = 1000.0 / fps
            return (imports and p95_ms > budget * 1.4 and
                    (over50 >= 3 or p75_ms > budget * 1.20))

        for fps, p75, p95 in ((30, 41.0, 48.0),
                              (45, 28.0, 34.0),
                              (60, 21.0, 26.0)):
            self.assertTrue(pressured(fps, p75, p95, 0, True))
            self.assertFalse(pressured(fps, p75, p95, 0, False))
            self.assertFalse(pressured(fps, 1000.0 / fps, p95, 0, True))
        # Existing severe-hitch fallback remains supported.
        self.assertTrue(pressured(60, 16.0, 40.0, 3, True))
        # Exactly on a boundary is not extra pressure.
        self.assertFalse(pressured(45, 1000.0 / 45.0 * 1.2,
                                  1000.0 / 45.0 * 1.4, 0, True))

    def test_only_tuned_on_periodic_sample_not_update(self):
        self.assertEqual(SOURCE.count("TuneAndroidUploadBudget(s);"), 1)
        self.assertIn('if (reason == "sample")', SOURCE)
        update = SOURCE.split("private void Update()", 1)[1].split("private bool CanRecordPeriodicDiagnostics()", 1)[0]
        self.assertNotIn("TuneAndroidUploadBudget(", update)

    def test_native_budget_observable_in_diagnostics(self):
        self.assertIn("public int asyncUploadTimeSliceMs;", SOURCE)
        self.assertIn("snapshot.asyncUploadTimeSliceMs = QualitySettings.asyncUploadTimeSlice;", SOURCE)


if __name__ == "__main__":
    unittest.main()
