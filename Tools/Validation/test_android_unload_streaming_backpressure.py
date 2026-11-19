#!/usr/bin/env python3
"""Source + scheduler checks: critical memory cleanup outranks cosmetic imports.

The tests model frame admission only. They are not Android/OOM measurements.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
GATE = (SCRIPTS / "AarisMobileStreamingFrameGate.cs").read_text()
RUNTIME = (SCRIPTS / "AarisMobileRuntime.cs").read_text()
GRAPH = (ROOT / "Assets/TerrainDemoScene_URP/ShaderGraphs/WaterDepthBased.shadergraph").read_text()
URP_HIGH = (ROOT / "Assets/TerrainDemoScene_URP/Settings/URP/URP-HighQuality Pipeline Asset.asset").read_text()


def simulate_cleanup_transition(cleanup_busy_by_frame, optional_jobs):
    """Models a nonblocking cleanup barrier followed by normal 1/frame pacing."""
    pending = list(optional_jobs)
    completed = []
    needs_settle_frame = False
    for frame, busy in enumerate(cleanup_busy_by_frame, start=1):
        if busy:
            needs_settle_frame = True
            continue
        if needs_settle_frame:
            needs_settle_frame = False
            continue
        if pending:
            completed.append((frame, pending.pop(0)))
    return completed, pending


class CriticalMemoryBackpressure(unittest.TestCase):
    def test_unload_reclamation_starts_before_optional_gate_is_notified(self):
        body = RUNTIME.split("if (lowMemoryPending)", 1)[1].split(
            'Record("low-memory")', 1)[0]
        self.assertIn("unloading = Resources.UnloadUnusedAssets();", body)
        self.assertIn(
            "AarisMobileStreamingFrameGate.DeferOptionalWorkDuringAssetCleanup(unloading);",
            body)
        self.assertLess(body.index("unloading = Resources.UnloadUnusedAssets();"),
                        body.index("DeferOptionalWorkDuringAssetCleanup(unloading)"))
        self.assertLess(body.index("DeferOptionalWorkDuringAssetCleanup(unloading)"),
                        body.index("assetUnloadStartedCount++;"))
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", body)
        self.assertNotIn("WaitForCompletion", body)
        self.assertNotIn("GC.Collect()", body)

    def test_native_cleanup_has_global_admission_priority(self):
        self.assertIn("private static AsyncOperation unusedAssetCleanup;", GATE)
        self.assertIn("internal static void DeferOptionalWorkDuringAssetCleanup(", GATE)
        # The modern gate handles instant completion as well as an in-flight
        # unload; both paths reserve the native completion frame.
        self.assertIn("if (cleanup == null) return;", GATE)
        self.assertIn("if (cleanup.isDone)", GATE)
        self.assertLess(GATE.index("if (cleanup.isDone)"),
                        GATE.index("unusedAssetCleanup = cleanup;"))
        self.assertIn("private static bool IsAssetCleanupPending()", GATE)
        self.assertIn("if (!unusedAssetCleanup.isDone) return true;", GATE)
        self.assertIn("unusedAssetCleanup = null;", GATE)
        self.assertIn("private static int cleanupCompletionFrame = -1;", GATE)
        self.assertIn("cleanupCompletionFrame = Time.frameCount;", GATE)
        self.assertIn("return cleanupCompletionFrame == Time.frameCount;", GATE)
        gate = GATE.split("internal static bool TryClaimOptionalModelBuild()", 1)[1].split(
            "internal static ResourceRequest TryStartOptionalLoadAsync", 1)[0]
        async_load = GATE.split("internal static ResourceRequest TryStartOptionalLoadAsync", 1)[1]
        self.assertRegex(gate,
                         r"if \(IsAssetCleanupPending\(\)\)\s*\{\s*"
                         r"CleanupDeferredAttempts\+\+;\s*return false;\s*\}")
        self.assertRegex(async_load,
                         r"if \(IsAssetCleanupPending\(\)\)\s*\{\s*"
                         r"CleanupDeferredAttempts\+\+;\s*return null;\s*\}")
        self.assertLess(gate.index("IsAssetCleanupPending()"),
                        gate.index("claimedFrame = frame;"))
        self.assertLess(async_load.index("IsAssetCleanupPending()"),
                        async_load.index("Resources.LoadAsync<T>(path)"))
        reset = GATE.split("private static void Reset()", 1)[1]
        self.assertIn("unusedAssetCleanup = null;", reset)
        self.assertIn("cleanupCompletionFrame = -1;", reset)

    def test_busy_cleanup_delays_optional_work_but_loses_nothing(self):
        names = ["foliage-model", "distant-building", "optional-audio", "vehicle-visual"]
        completed, pending = simulate_cleanup_transition(
            [True, True, True, False, False, False, False, False], names)
        self.assertEqual(pending, [])
        self.assertEqual([n for _, n in completed], names)
        self.assertEqual(completed[0][0], 5)
        self.assertEqual(len({frame for frame, _ in completed}), len(names))

    def test_idle_gate_does_not_insert_extra_frame(self):
        completed, pending = simulate_cleanup_transition([False, False], ["a", "b"])
        self.assertEqual(completed, [(1, "a"), (2, "b")])
        self.assertFalse(pending)

    def test_budget_remains_android_only_and_source_visuals_unchanged(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", GATE)
        self.assertIn("bool constrained = IsConstrainedDevice();", GATE)
        self.assertNotIn("Destroy(", GATE)
        self.assertNotIn("QualitySettings", GATE)
        self.assertNotIn("TerrainData", GATE)
        self.assertNotIn("renderScale", GATE)

    def test_observable_reasons_for_busy_streaming_frames(self):
        # Counters distinguish a free-frame collision, a running asset
        # import and a critical-memory cleanup. These are attempted
        # admissions, never elapsed GPU time or total pending jobs.
        for token in ("CleanupDeferredAttempts", "SameFrameDeferredAttempts",
                      "ImportDeferredAttempts"):
            self.assertIn("internal static int " + token + " { get; private set; }", GATE)
            self.assertIn(token + "++", GATE)
        self.assertIn(
            "CleanupDeferredAttempts = SameFrameDeferredAttempts = ImportDeferredAttempts = 0;",
            GATE)
        for field in ("optionalStreamCleanupDeferrals",
                      "optionalStreamFrameDeferrals",
                      "optionalStreamImportDeferrals"):
            self.assertIn("public int " + field, RUNTIME)
            self.assertIn("s." + field + " =", RUNTIME)

    def test_actual_urp_state_is_reported_without_mutating_water_passes(self):
        for field in ("urpMsaaSamples", "urpShadowCascades",
                      "urpMainLightShadowmapResolution", "urpShadowDistance",
                      "urpRequiresDepthTexture", "urpRequiresOpaqueTexture",
                      "urpSupportsHdr"):
            self.assertIn(field, RUNTIME)
            self.assertIn("s." + field + " =", RUNTIME)
        self.assertIn("pipeline.mainLightShadowmapResolution", RUNTIME)
        self.assertIn("pipeline.supportsCameraDepthTexture", RUNTIME)
        self.assertIn("pipeline.supportsCameraOpaqueTexture", RUNTIME)
        self.assertNotIn("pipeline.supportsCameraDepthTexture =", RUNTIME)
        self.assertNotIn("pipeline.supportsCameraOpaqueTexture =", RUNTIME)

    def test_water_shader_graph_requires_scene_color_and_depth_copies(self):
        # The map contains a water Shader Graph with *both* scene texture
        # inputs: switching off the URP passes without verifying scene usage
        # could silently break refraction/depth water appearance.
        self.assertIn("UnityEditor.ShaderGraph.SceneColorNode", GRAPH)
        self.assertIn("UnityEditor.ShaderGraph.SceneDepthNode", GRAPH)
        self.assertIn("m_RequireDepthTexture: 1", URP_HIGH)
        self.assertIn("m_RequireOpaqueTexture: 1", URP_HIGH)
        self.assertNotIn("supportsCameraDepthTexture =", RUNTIME)
        self.assertNotIn("supportsCameraOpaqueTexture =", RUNTIME)


if __name__ == "__main__":
    unittest.main(verbosity=2)
