"""Source-contract checks for the reversible emergency URP render-scale path.

These are not Unity import, shader, Android GPU, or measured FPS tests.
"""
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[3]
RUNTIME = ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs"


class EmergencyRenderScaleSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = RUNTIME.read_text(encoding="utf-8")

    def test_low_ram_android_profile_only(self):
        s = self.source
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR\n            ApplyMobileMemoryProfile();", s)
        self.assertIn("if (systemRamMb <= 0 || systemRamMb > 4096) return;", s)
        self.assertIn("emergencyRenderScaleFloor = constrained ? 0.90f : 0.94f;", s)
        self.assertIn("allowEmergencyRenderScale = true;", s)

    def test_sustained_pressure_only_and_reversible(self):
        s = self.source
        self.assertIn("pressureSamples >= 2 && performanceLevel < 2", s)
        self.assertIn("frameCount < frameTimes.Length || Time.unscaledTime < 15f", s)
        self.assertIn("nextPerformanceChangeAt = Time.unscaledTime + 12f;", s)
        self.assertIn("allowEmergencyRenderScale && performanceLevel >= 2", s)
        self.assertIn("Mathf.Min(originalRenderScale, emergencyRenderScaleFloor)", s)
        self.assertIn(": originalRenderScale;", s)
        self.assertIn("originalRenderScale = mobilePipeline.renderScale;", s)
        self.assertIn("mobilePipeline.renderScale = originalRenderScale;", s)
        self.assertIn("if (!mobileProfileApplied) return;", s)

    def test_telemetry_and_far_world_are_preserved(self):
        s = self.source
        self.assertIn("public bool emergencyRenderScaleActive;", s)
        self.assertIn("public float renderScale;", s)
        self.assertIn("s.renderScale = mobilePipeline != null ? mobilePipeline.renderScale : 1f;", s)
        self.assertIn("s.emergencyRenderScaleActive = mobileProfileApplied", s)
        self.assertNotIn("farClipPlane =", s)
        self.assertNotIn("treeDistance =", s)
        self.assertIn("terrain.treeBillboardDistance = originalTreeBillboardDistances[i];", s)


if __name__ == "__main__":
    unittest.main()
