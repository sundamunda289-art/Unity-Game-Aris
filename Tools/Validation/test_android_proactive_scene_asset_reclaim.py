from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
RUNTIME = (SCRIPTS / "AarisMobileRuntime.cs").read_text()
STREAMER = (SCRIPTS / "AarisAdditiveChunkStreamer.cs").read_text()
GATE = (SCRIPTS / "AarisMobileStreamingFrameGate.cs").read_text()
PRESSURE = (SCRIPTS / "AarisAndroidMemoryPressure.cs").read_text()


class AndroidProactiveSceneAssetReclaimTests(unittest.TestCase):
    def test_android_memory_signal_is_low_frequency_and_threshold_based(self):
        self.assertIn("private const float PollSeconds = 5f;", PRESSURE)
        self.assertIn('new AndroidJavaObject("android.app.ActivityManager$MemoryInfo")', PRESSURE)
        self.assertIn('memoryInfo.Get<long>("availMem")', PRESSURE)
        self.assertIn('memoryInfo.Get<long>("threshold")', PRESSURE)
        self.assertIn('memoryInfo.Get<bool>("lowMemory")', PRESSURE)
        self.assertIn("MinimumSoftHeadroomBytes", PRESSURE)
        self.assertNotIn("Update()", PRESSURE)

    def test_only_real_owned_scene_retirement_creates_reclaim_work(self):
        block = STREAMER[STREAMER.index("if (inFlight != null)"):
                         STREAMER.index("BuildSpatialIndexSlice();")]
        self.assertIn("bool retiredOwnedScene = states[finished].loadedByThisStreamer;", block)
        self.assertIn("if (retiredOwnedScene)", block)
        self.assertIn("AarisMobileStreamingFrameGate.NotifyAdditiveSceneRetired();", block)
        self.assertLess(block.index("if (!inFlight.isDone) return;"),
                        block.index("NotifyAdditiveSceneRetired"))

    def test_reclaim_requires_pressure_dead_scene_calm_frame_and_gate(self):
        self.assertIn("AarisMobileStreamingFrameGate.RetiredAdditiveSceneCount > 0", RUNTIME)
        self.assertIn("AarisAndroidMemoryPressure.TrySample(out bool memoryPressure)", RUNTIME)
        self.assertIn("memoryPressure && AarisMobileStreamingFrameGate.RetiredAdditiveSceneCount > 0", RUNTIME)
        self.assertIn("SceneRetirementCalmSeconds = 2f", RUNTIME)
        self.assertIn("ProactiveAssetCleanupCooldownSeconds = 30f", RUNTIME)
        self.assertIn("Time.smoothDeltaTime <=", RUNTIME)
        self.assertIn("CurrentPacedFrameBudgetSeconds() * 1.08f", RUNTIME)
        self.assertIn("TryClaimUnusedAssetCleanupFrame()", RUNTIME)
        self.assertIn("unloading = Resources.UnloadUnusedAssets();", RUNTIME)
        self.assertIn("DeferOptionalWorkDuringAssetCleanup(unloading);", RUNTIME)

    def test_retirement_signal_state_is_owned_by_the_frame_gate(self):
        self.assertIn("private static int retiredAdditiveScenes;", GATE)
        self.assertIn("private static float lastAdditiveSceneRetirementAt = -1f;", GATE)
        self.assertIn("internal static void NotifyAdditiveSceneRetired()", GATE)
        self.assertIn("internal static int RetiredAdditiveSceneCount", GATE)
        self.assertIn("internal static float LastAdditiveSceneRetirementAt", GATE)
        self.assertIn("internal static void ClearRetiredAdditiveSceneSignals()", GATE)
        self.assertNotIn("NotifyAdditiveSceneRetired()", RUNTIME)

    def test_cleanup_admission_reuses_existing_native_work_fences(self):
        method = GATE[GATE.index("internal static bool TryClaimUnusedAssetCleanupFrame()"):
                      GATE.index("internal static bool TryClaimOptionalModelBuild()")]
        self.assertIn("return TryClaimNonessentialDiagnosticFrame();", method)
        diagnostic = GATE[GATE.index("internal static bool TryClaimNonessentialDiagnosticFrame()"):
                          GATE.index("internal static bool TryClaimUnusedAssetCleanupFrame()")]
        for token in (
            "IsAssetCleanupPending()",
            "IsNativeTeardownSettleFrame()",
            "IsAdditiveSceneBusy()",
            "IsOptionalImportBusy()",
            "ShouldDeferForFramePressure(frame)",
        ):
            self.assertIn(token, diagnostic)

    def test_critical_low_memory_path_remains_immediate_and_covers_retirements(self):
        low = RUNTIME[RUNTIME.index("if (lowMemoryPending)"):
                      RUNTIME.index("// SceneManager.UnloadSceneAsync destroys scene objects")]
        self.assertIn("Resources.UnloadUnusedAssets();", low)
        self.assertIn(
            "AarisMobileStreamingFrameGate.ClearRetiredAdditiveSceneSignals();",
            low)
        self.assertIn("proactiveAssetCleanupPending = false;", low)
        self.assertNotIn("GC.Collect", low)

    def test_no_visual_quality_or_gameplay_reduction(self):
        combined = PRESSURE + "\n" + RUNTIME[RUNTIME.index("SceneRetirementCalmSeconds"):
                                              RUNTIME.index("#if UNITY_EDITOR || DEVELOPMENT_BUILD")]
        for token in (
            "renderScale =",
            "treeDistance =",
            "detailObjectDistance =",
            "shadowDistance =",
            "QualitySettings.SetQualityLevel",
            "Application.targetFrameRate =",
        ):
            self.assertNotIn(token, combined)


if __name__ == "__main__":
    unittest.main()
