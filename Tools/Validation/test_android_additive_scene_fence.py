"""Android opt-in scene integration fence contracts (not an APK benchmark)."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text()
STREAMER = (SRC / "AarisAdditiveChunkStreamer.cs").read_text()
HARNESS = (ROOT / "Tools/Validation/StreamingAdmissionTests.cs").read_text()


class AdditiveSceneFence(unittest.TestCase):
    def test_opt_in_loader_registers_async_scene_without_blocking_queue(self):
        self.assertIn(
            "if (load) AarisMobileStreamingFrameGate.TrackAdditiveSceneLoad(operation);",
            STREAMER,
        )
        self.assertIn("operation.priority = -1;", STREAMER)
        self.assertNotIn("allowSceneActivation = false", STREAMER)
        self.assertNotIn("RuntimeInitializeOnLoadMethod", STREAMER)

    def test_shared_admission_gates_optional_import_and_diagnostics(self):
        for token in (
            "private static bool IsAdditiveSceneLoadBusy()",
            "additiveIntegrationFrame = Time.frameCount;",
            "AdditiveSceneDeferredAttempts++;",
            "additiveLoads.Clear();",
            "if (IsAdditiveSceneLoadBusy()) return false;",
        ):
            self.assertIn(token, GATE)
        self.assertIn("private static void AdditiveSceneFence()", HARNESS)
        self.assertIn("AdditiveSceneFence(); DiagnosticImportFence();", HARNESS)

    def test_scene_and_native_visuals_are_preserved(self):
        for forbidden in (
            "QualitySettings", "TerrainData", "Renderer.enabled",
            "UnloadUnusedAssets(", "allowSceneActivation = false",
        ):
            self.assertNotIn(forbidden, STREAMER)


if __name__ == "__main__":
    unittest.main(verbosity=2)
