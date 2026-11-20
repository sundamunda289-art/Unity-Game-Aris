#!/usr/bin/env python3
"""Source-level Android parked-vehicle teardown backpressure regression.

This tests scheduling contracts, not native Unity frame time or handset FPS.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
CARS = (SRC / "AarisVehicleWorld.cs").read_text(encoding="utf-8")
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text(encoding="utf-8")


def should_defer_optional_build(unloaded, constrained_gpu):
    return unloaded > 0 and constrained_gpu


class AndroidVehicleRetirementFrame(unittest.TestCase):
    def setUp(self):
        self.update = CARS.split("private void Update()", 1)[1].split(
            "private AarisVehicleRecord FarthestReplaceableParkedCar(", 1)[0]

    def test_original_parked_only_retirement_is_preserved(self):
        s = self.update
        self.assertIn("dist2 > UnloadRadius * UnloadRadius && record.visible.CanPark", s)
        self.assertIn("unloaded < unloadBudgetPerPoll", s)
        self.assertLess(s.index("record.damage01 = record.visible.Damage01;"),
                        s.index("Destroy(record.visible.gameObject);"))
        self.assertLess(s.index("Destroy(record.visible.gameObject);"),
                        s.index("if (unloaded > 0 && AarisMobileStreamingFrameGate.IsConstrainedDevice())"))
        self.assertIn("record.visible = null;", s)

    def test_android_only_shared_native_frame_reservation(self):
        s = self.update
        retire = s.index("if (unloaded > 0 && AarisMobileStreamingFrameGate.IsConstrainedDevice())")
        frame = s.index("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();", retire)
        returns = s.index("return;", frame)
        new_import = s.index("PumpAuthoredBodyLoads();", returns)
        build = s.index("if (!Build(record, pos))", new_import)
        self.assertLess(retire, frame)
        self.assertLess(frame, returns)
        self.assertLess(returns, new_import)
        self.assertLess(new_import, build)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", s[s.index("else active++;"):frame])
        self.assertIn("#endif", s[returns:new_import])
        self.assertIn("internal static void ReservePhysicsStreamingFrame()", GATE)
        self.assertIn("claimedFrame = Time.frameCount;", GATE)
        self.assertNotIn("QualitySettings.", s[retire:new_import])
        self.assertNotIn("Destroy(", s[retire:new_import])

    def test_retirement_never_suppresses_new_models_when_not_needed(self):
        self.assertEqual(should_defer_optional_build(0, True), False)
        self.assertEqual(should_defer_optional_build(0, False), False)
        self.assertEqual(should_defer_optional_build(2, False), False)
        self.assertTrue(should_defer_optional_build(1, True))
        self.assertTrue(should_defer_optional_build(2, True))
        self.assertIn("nextPoll = now + .3f;", self.update)
        self.assertIn("float nearestDistanceSq = LoadRadius * LoadRadius;", self.update)
        self.assertIn("if (!Build(record, pos))", self.update)
        self.assertIn("if (parkedVictim != null)", self.update)

    def test_original_car_counts_and_visibility_distances_unchanged(self):
        for code in (
            "private const int TargetVehicles = 44;",
            "private const int MaximumNearbyVehicles = 10;",
            "private const float LoadRadius = 105f;",
            "private const float UnloadRadius = 160f;",
            "private int spawnBudgetPerPoll = 2;",
            "private int unloadBudgetPerPoll = 4;",
        ):
            self.assertIn(code, CARS)


if __name__ == "__main__":
    unittest.main(verbosity=2)
