#!/usr/bin/env python3
"""Regression guards for non-blocking Android player diagnostics."""
from pathlib import Path
import unittest

SOURCE = Path(__file__).resolve().parents[2] / (
    "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs"
)


class MobileDiagnosticsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")

    def test_main_thread_never_waits_on_report(self):
        self.assertNotIn("GetAwaiter().GetResult()", self.source)
        self.assertNotIn(".Wait()", self.source)

    def test_file_rotation_is_async_on_android(self):
        awake = self.source.split("private void Awake()", 1)[1]
        awake = awake.split("private IEnumerator Start()", 1)[0]
        self.assertIn("Task.Run(() => InitializeLogFile(initialPath))", awake)
        self.assertNotIn("File.Copy(", awake)
        self.assertNotIn("File.WriteAllText(", awake)

    def test_background_writes_remain_ordered(self):
        record = self.source.split("private void Record(string reason)", 1)[1]
        record = record.split("private static string InitializeLogFile", 1)[0]
        self.assertIn("pendingLogWrite.ContinueWith(previous =>", record)
        self.assertIn("TaskScheduler.Default", record)
        self.assertIn("previous.Result ?? AppendReport(path, report)", record)

    def test_sampling_does_not_create_unbounded_io_backlog(self):
        record = self.source.split("private void Record(string reason)", 1)[1]
        record = record.split("private static string InitializeLogFile", 1)[0]
        self.assertIn('reason == "sample"', record)
        self.assertIn("!pendingLogWrite.IsCompleted", record)

    def test_read_only_source_visuals_are_untouched(self):
        self.assertIn("private void ConfigureTerrainInstanceRenderer()", self.source)
        self.assertIn("private void ConfigureLowMemoryVegetation()", self.source)


if __name__ == "__main__":
    unittest.main()
