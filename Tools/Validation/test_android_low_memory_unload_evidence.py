#!/usr/bin/env python3
"""Unity 6 low-memory asset cleanup observability regression.

Static source guards only; not Unity compilation or device measurements.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text()

class AssetUnloadEvidence(unittest.TestCase):
    def test_low_memory_still_cleans_up_without_blocking_main_thread(self):
        update = SOURCE.split("private void Update()", 1)[1].split(
            "private void OnLowMemory()", 1)[0]
        low = update.split("if (lowMemoryPending)", 1)[1]
        self.assertIn("Resources.UnloadUnusedAssets();", low)
        self.assertIn("Time.unscaledTime >= unloadAfter", low)
        self.assertIn("unloading == null || unloading.isDone", low)
        self.assertIn("unloadAfter = Time.unscaledTime + 30;", low)
        self.assertIn('Record("low-memory");', low)
        self.assertIn("assetUnloadStartedCount++;", low)
        self.assertIn("assetUnloadSuppressedCount++;", low)
        self.assertNotIn("WaitForCompletion", update)
        self.assertNotIn("GC.Collect()", update)

    def test_completion_report_is_one_shot_and_nonblocking(self):
        update = SOURCE.split("private void Update()", 1)[1].split(
            "private void OnLowMemory()", 1)[0]
        section = update.split("if (assetUnloadStartedRealtime >= 0f", 1)[1].split(
            "if (lowMemoryPending)", 1)[0]
        for part in ("unloading != null && unloading.isDone",
                     "Time.realtimeSinceStartup - assetUnloadStartedRealtime",
                     "assetUnloadStartedRealtime = -1f;",
                     "assetUnloadCompletedCount++;",
                     'Record("asset-unload-complete");'):
            self.assertIn(part, section)
        self.assertLess(section.index("assetUnloadStartedRealtime = -1f;"),
                        section.index('Record("asset-unload-complete");'))
        self.assertNotIn("while (", section)
        self.assertNotIn("WaitForCompletion", section)

    def test_json_marks_unknown_elapsed_and_disambiguates_wall_clock(self):
        sample = SOURCE.split("private sealed class Sample", 1)[1].split(
            "private int warningCount", 1)[0]
        record = SOURCE.split("private void Record(string reason)", 1)[1]
        for name in ("assetUnloadStartedCount", "assetUnloadCompletedCount",
                     "assetUnloadSuppressedCount", "assetUnloadInProgress"):
            self.assertIn(name, sample)
        for name in ("assetUnloadStartedCount", "assetUnloadCompletedCount",
                     "assetUnloadSuppressedCount"):
            self.assertIn(name + " = " + name, record)
        self.assertIn("assetUnloadInProgress = unloading != null && !unloading.isDone", record)
        self.assertIn("public float lastAssetUnloadElapsedMs = -1f;", sample)
        self.assertIn("lastAssetUnloadElapsedMs = lastAssetUnloadElapsedMs,", record)
        self.assertIn("it is NOT a measurement of CPU time", SOURCE)

    def test_existing_render_and_telemetry_contracts(self):
        for part in ("private void ConfigureTerrainInstanceRenderer()",
                     "private void ConfigureLowMemoryVegetation()",
                     "Profiler.GetAllocatedMemoryForGraphicsDriver();",
                     'Record("frame-stall")',
                     "Application.lowMemory += OnLowMemory;",
                     "Application.lowMemory -= OnLowMemory;"):
            self.assertIn(part, SOURCE)

if __name__ == "__main__":
    unittest.main(verbosity=2)
