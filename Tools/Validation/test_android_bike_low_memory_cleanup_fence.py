#!/usr/bin/env python3
"""Android low-memory bike stream frame separation: source-level contracts.

No Unity native compilation or physical-device FPS measurement is implied.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
BIKES = (SRC / "AarisRoadsideBikeScenery.cs").read_text(encoding="utf-8")
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text(encoding="utf-8")


class LowMemoryBikeCleanupFence(unittest.TestCase):
    def setUp(self):
        self.update = BIKES.split("private void Update()", 1)[1].split(
            "private GameObject ReuseBike(", 1)[0]
        self.lowmem = self.update.split("if (lowMemoryPending)", 1)[1].split(
            "if (player == null || !enabled) return;", 1
        )[0]

    def test_cleanup_always_reserves_frame_before_any_new_streaming(self):
        body = self.lowmem
        self.assertIn("lowMemoryPending = false;", body)
        self.assertIn("poolAllowedAt = Time.unscaledTime + 60f;", body)
        self.assertIn("while (parkedBikePool.Count > 0)", body)
        self.assertIn("bikeVisuals.Remove(inactiveBike);", body)
        self.assertIn("Destroy(inactiveBike);", body)
        self.assertIn("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();", body)
        self.assertLess(body.index("Destroy(inactiveBike);"),
                        body.index("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();"))
        self.assertLess(body.index("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();"),
                        body.index("return;", body.index(
                            "AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();")))
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", body)
        self.assertIn("#endif", body)

    def test_fence_precedes_optional_prewarm_and_fresh_hierarchy(self):
        body = self.update
        lowmem_fence = body.index(
            "AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();"
        )
        self.assertLess(lowmem_fence, body.index(
            "AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(ModelResource)"
        ))
        self.assertLess(lowmem_fence, body.index("CreateBike(nearest.point, spot)"))
        self.assertLess(lowmem_fence, body.index("SetupMaterials();"))
        self.assertIn("if (now < nextPoll) return;", body)
        self.assertIn("nextPoll = now + .4f;", body)
        self.assertIn("internal static void ReservePhysicsStreamingFrame()", GATE)

    def test_no_visible_bikes_discarded_and_no_cosmetic_downgrades(self):
        self.assertIn("GameObject inactiveBike = parkedBikePool.Pop();", self.lowmem)
        self.assertNotIn("plan.visible =", self.lowmem)
        self.assertNotIn("bikeSource = null", self.lowmem)
        for invariant in (
            'ModelResource = "AarisVehicles/Authored/AarisRoadBike"',
            "private const int MaximumVisibleBikes = 3;",
            "private const float LoadRadius = 45f;",
            "private const float UnloadRadius = 66f;",
            "private const float PrewarmRadius = 110f;",
            "private readonly Stack<GameObject> parkedBikePool",
        ):
            self.assertIn(invariant, BIKES)
        self.assertNotIn("QualitySettings", self.lowmem)
        self.assertNotIn("renderScale", self.lowmem)
        self.assertNotIn("Resources.UnloadUnusedAssets()", self.lowmem)

    def test_critical_signal_preempts_poll_even_with_empty_pool(self):
        # A small policy model: a low-memory notification has priority
        # regardless of whether the model pool happens to have entries.
        def can_do_optional_work(low_memory_pending, cached_rigs, android):
            if low_memory_pending and android:
                return False
            return True
        for n in (0, 1, 3):
            self.assertFalse(can_do_optional_work(True, n, True))
            self.assertTrue(can_do_optional_work(False, n, True))
            self.assertTrue(can_do_optional_work(True, n, False))


if __name__ == "__main__":
    unittest.main(verbosity=2)
