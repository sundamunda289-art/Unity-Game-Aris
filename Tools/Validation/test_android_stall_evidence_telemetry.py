#!/usr/bin/env python3
"""Source guards for Android diagnostic counters; not device profiling."""
from pathlib import Path
import unittest

SRC=(Path(__file__).resolve().parents[2]/"Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text()

class StallEvidence(unittest.TestCase):
    def test_fixed_tick_count_is_android_only_and_alloc_free(self):
        method=SRC.split("private void FixedUpdate()",1)[1].split("private void Update()",1)[0]
        self.assertIn("physicsStepsBeforeRender++;",method)
        self.assertNotIn("new ",method)
        self.assertNotIn("Find",method)
        before=SRC.split("private void FixedUpdate()",1)[0]
        self.assertTrue(before.rstrip().endswith("#if UNITY_ANDROID && !UNITY_EDITOR"))
    def test_counts_fixed_ticks_between_render_frames(self):
        update=SRC.split("private void Update()",1)[1].split("private void OnLowMemory()",1)[0]
        for text in ("int physicsTicks = physicsStepsBeforeRender;", "physicsStepsBeforeRender = 0;",
                     "fixedStepsInWindow += physicsTicks;", "peakFixedStepsInWindow = physicsTicks;"):
            self.assertIn(text,update)
    def test_graphics_driver_query_is_sample_only(self):
        record=SRC.split("private void Record(string reason)",1)[1].split("private static string InitializeLogFile",1)[0]
        self.assertIn('if (reason == "sample")',record)
        self.assertIn("Profiler.GetAllocatedMemoryForGraphicsDriver();",record)
        self.assertIn("estimatedGraphicsDriverBytes = -1L",record)
        query=record.index("Profiler.GetAllocatedMemoryForGraphicsDriver();")
        sample=record.index('if (reason == "sample")')
        self.assertLess(sample,query)
        self.assertNotIn("GetAllocatedMemoryForGraphicsDriver()",SRC.split("private void Update()",1)[1].split("private void OnLowMemory()",1)[0])
    def test_report_fields_not_misrepresented_as_pss(self):
        for field in ("peakFixedStepsPerRender","fixedStepsInSample","estimatedGraphicsDriverBytes"):
            self.assertIn(field,SRC)
        self.assertIn("s.androidPssKB = -1;",SRC)
        self.assertIn("NOT Android PSS",SRC)
    def test_nonvisual_and_restore_contract(self):
        self.assertIn("Time.maximumDeltaTime = originalMaximumDeltaTime;",SRC)
        self.assertIn("private void ConfigureTerrainInstanceRenderer()",SRC)
        self.assertIn("private void ConfigureLowMemoryVegetation()",SRC)

if __name__=="__main__":
    unittest.main(verbosity=2)
