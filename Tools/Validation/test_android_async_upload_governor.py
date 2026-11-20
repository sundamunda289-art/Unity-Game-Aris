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
        self.assertIn("importsObserved && snapshot.framesOver50Ms >= 3", SOURCE)
        self.assertIn("snapshot.p95FrameMs > targetFrameMs * 1.4f", SOURCE)
        self.assertIn("asyncUploadRecoveryWindows >= 2", SOURCE)
        self.assertIn("current != appliedAsyncUploadTimeSlice", SOURCE)

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
