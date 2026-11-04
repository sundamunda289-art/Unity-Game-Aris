"""Read-only regression checks for the conditional Android static LOD crossfade fallback.

No claims of executed Unity rendering, measured overdraw, or device FPS.
"""
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
RUNTIME = ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs"


class StaticLodCrossFadeFallbackTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = RUNTIME.read_text(encoding="utf-8")

    def test_low_ram_only_and_sustained_pressure_gated(self):
        s = self.source
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR\n            ApplyMobileMemoryProfile();", s)
        self.assertIn("if (systemRamMb <= 0 || systemRamMb > 4096) return;", s)
        self.assertIn("CaptureStaticLodFadeBaseline();", s)
        self.assertIn("suppressStaticLodCrossFadeUnderPressure = true;", s)
        self.assertIn("SetStaticLodCrossFadeSuppressed(suppressStaticLodCrossFadeUnderPressure &&\n                performanceLevel >= 2);", s)
        self.assertIn("pressureSamples >= 2 && performanceLevel < 2", s)
        self.assertIn("nextPerformanceChangeAt = Time.unscaledTime + 12f;", s)

    def test_only_three_lod_scene_crossfade_groups_change(self):
        s = self.source
        self.assertIn("FindObjectsByType<LODGroup>(FindObjectsSortMode.None)", s)
        self.assertIn("group.enabled && group.lodCount == 3 &&", s)
        self.assertIn("group.fadeMode == LODFadeMode.CrossFade", s)
        self.assertIn("group.lodCount != 3", s)
        self.assertIn("LODFadeMode mode = suppress ? LODFadeMode.None : originalStaticFadeModes[i];", s)
        self.assertIn("bool animate = !suppress && originalStaticAnimateCrossFading[i];", s)
        self.assertNotIn("group.SetLODs(", s)
        self.assertNotIn("group.ForceLOD(", s)
        self.assertNotIn("group.RecalculateBounds(", s)

    def test_original_crossfade_settings_restore_on_recovery_and_destroy(self):
        s = self.source
        self.assertIn("originalStaticFadeModes[index] = group.fadeMode;", s)
        self.assertIn("originalStaticAnimateCrossFading[index] = group.animateCrossFading;", s)
        self.assertIn("SetStaticLodCrossFadeSuppressed(false);", s)
        self.assertIn("if (group.fadeMode != mode) group.fadeMode = mode;", s)
        self.assertIn("if (group.animateCrossFading != animate) group.animateCrossFading = animate;", s)
        self.assertIn("staticLodCrossFadeSuppressed = suppress;", s)

    def test_distant_lods_and_telemetry_remain(self):
        s = self.source
        self.assertNotIn("terrain.treeDistance =", s)
        self.assertNotIn("farClipPlane =", s)
        self.assertIn("public bool emergencyRenderScaleActive;", s)
        self.assertIn("public bool staticLodCrossFadeSuppressed;", s)
        self.assertIn("public int staticLodCrossFadeGroupCount;", s)
        self.assertIn("s.staticLodCrossFadeSuppressed = staticLodCrossFadeSuppressed;", s)
        self.assertIn("s.staticLodCrossFadeGroupCount = staticLodGroups != null ? staticLodGroups.Length : 0;", s)


    def test_foreground_resume_discards_stale_frame_pressure(self):
        s = self.source
        self.assertIn("private void ResetFrameObservation()", s)
        self.assertIn("Array.Clear(frameTimes, 0, frameTimes.Length);", s)
        self.assertIn("frameIndex = frameCount = pressureSamples = recoverySamples = 0;", s)
        self.assertIn("nextPerformanceChangeAt = Time.unscaledTime + 6f;", s)
        self.assertIn("if (cap != observedTargetFps)\n            {\n                observedTargetFps = cap;\n                ResetFrameObservation();", s)
        self.assertIn("private void OnApplicationPause(bool paused)", s)
        self.assertIn('if (paused) Record("pause");', s)
        self.assertIn('ResetFrameObservation();\n                Record("resume");', s)
        self.assertIn("private void OnApplicationFocus(bool focused)", s)
        self.assertIn("if (focused) ResetFrameObservation();", s)
        self.assertIn("performanceLevel >= 2", s)

if __name__ == "__main__":
    unittest.main()
