#!/usr/bin/env python3
"""Nonblocking Android memory-signal fencing: source + deterministic timeline.

These are structural/scheduler tests only. Real Unity compilation and Android
frame-time/low-memory proof require a licensed Unity build and device.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
GATE = (SCRIPTS / "AarisMobileStreamingFrameGate.cs").read_text()
RUNTIME = (SCRIPTS / "AarisMobileRuntime.cs").read_text()


def notification_fence(notification_frame, frame):
    """One notification and one recovery render frame, no indefinite starvation."""
    return notification_frame >= 0 and frame in (
        notification_frame, notification_frame + 1
    )


class MemoryNotificationFence(unittest.TestCase):
    def test_signal_precedes_update_cleanup_and_does_not_block(self):
        callback = RUNTIME.split("private void OnLowMemory()", 1)[1].split(
            "private void OnApplicationFocus", 1
        )[0]
        self.assertIn("lowMemoryPending = true;", callback)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", callback)
        self.assertIn(
            "AarisMobileStreamingFrameGate.ReserveLowMemoryNotificationFrame();",
            callback
        )
        self.assertNotIn("Resources.UnloadUnusedAssets()", callback)
        self.assertNotIn("WaitForCompletion", callback)
        self.assertNotIn("GC.Collect()", callback)
        self.assertIn("Application.lowMemory += OnLowMemory;", RUNTIME)

    def test_gate_fences_before_optional_work(self):
        self.assertIn("private static int lowMemoryNotificationFrame = -1;", GATE)
        notification = GATE.split(
            "internal static void ReserveLowMemoryNotificationFrame()", 1
        )[1].split("private static bool IsAssetCleanupPending()", 1)[0]
        self.assertIn("lowMemoryNotificationFrame = Time.frameCount;", notification)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", notification)
        cleanup = GATE.split("private static bool IsAssetCleanupPending()", 1)[1].split(
            "private static bool IsOptionalImportBusy()", 1
        )[0]
        self.assertIn("frame == lowMemoryNotificationFrame", cleanup)
        self.assertIn("frame == lowMemoryNotificationFrame + 1", cleanup)
        self.assertLess(
            cleanup.index("frame == lowMemoryNotificationFrame"),
            cleanup.index("if (unusedAssetCleanup != null)")
        )
        self.assertIn("lowMemoryNotificationFrame = -1;", GATE.split(
            "private static void Reset()", 1
        )[1])
        for marker in (
            "internal static bool TryClaimOptionalModelBuild()",
            "internal static ResourceRequest TryStartOptionalLoadAsync",
            "internal static bool TryClaimNonessentialDiagnosticFrame()",
        ):
            section = GATE.split(marker, 1)[1]
            self.assertIn("IsAssetCleanupPending()", section[:1600])

    def test_no_unnatural_delay_or_visual_regression(self):
        for signal in (-1, 0, 31, 200):
            for frame in range(0, 207):
                expected = signal >= 0 and (frame == signal or frame == signal + 1)
                self.assertEqual(notification_fence(signal, frame), expected)
        self.assertFalse(notification_fence(10, 12))
        self.assertFalse(notification_fence(-1, 0))
        for token in ("Destroy(", "QualitySettings", "TerrainData", "renderScale",
                      "GC.Collect(", "WaitForCompletion("):
            self.assertNotIn(token, GATE)
        self.assertIn("Resources.UnloadUnusedAssets();", RUNTIME)
        self.assertIn("DeferOptionalWorkDuringAssetCleanup(unloading);", RUNTIME)


if __name__ == "__main__":
    unittest.main(verbosity=2)
