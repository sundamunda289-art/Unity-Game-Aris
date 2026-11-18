#!/usr/bin/env python3
"""Static regression guards for Android boot-file I/O (Unity 6)."""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs")


class AndroidStartupDiagnosticsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = SOURCE.read_text(encoding="utf-8")

    def test_android_startup_file_rotation_is_background(self):
        awake = self.text.split("private void Awake()", 1)[1].split(
            "private IEnumerator Start()", 1)[0]
        self.assertIn("Task.Run(() => InitializeLogFile(initialPath))", awake)
        self.assertNotIn("File.Copy(", awake)
        self.assertNotIn("File.WriteAllText(", awake)
        self.assertNotIn("Directory.CreateDirectory(", awake)

    def test_first_report_is_queued_after_initialize(self):
        record = self.text.split("private void Record(string reason)", 1)[1]
        self.assertIn('reason != "start")', record)
        self.assertNotIn('if (reason != "start")', record)
        self.assertIn("pendingLogWrite = previous == null", record)
        self.assertIn("previous.ContinueWith(completed =>", record)
        self.assertIn("TaskScheduler.Default", record)

    def test_existing_stall_capture_preserved(self):
        self.assertIn('Record("frame-stall")', self.text)
        self.assertIn("dt >= 0.75f", self.text)
        self.assertIn("FrameTimingManager.CaptureFrameTimings()", self.text)

    def test_no_visual_changes(self):
        self.assertIn("private void ConfigureTerrainInstanceRenderer()", self.text)
        self.assertIn("private void ConfigureLowMemoryVegetation()", self.text)


if __name__ == "__main__":
    unittest.main()
