#!/usr/bin/env python3
"""Low-RAM Android bike-retirement frame backpressure source contracts.

These tests do not measure Android FPS, Unity native teardown, or visual pop-in.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
BIKES = (SRC / "AarisRoadsideBikeScenery.cs").read_text(encoding="utf-8")
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text(encoding="utf-8")


def defer_new_rig(unloaded, have_parked_victim, have_pooled, constrained):
    return unloaded > 0 and not have_parked_victim and not have_pooled and constrained


class BikeRetirementFrame(unittest.TestCase):
    def setUp(self):
        self.poll = BIKES.split("private void Update()", 1)[1].split(
            "private GameObject ReuseBike(", 1)[0]

    def test_teardown_stays_before_optional_new_hierarchy(self):
        body = self.poll
        retire = body.index("Destroy(retired);")
        guard = body.index("if (unloaded > 0 && parkedVictim == null &&")
        reserve = body.index("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();", guard)
        next_probe = body.index("nearest.nextProbe = now + 3f;", guard)
        placement = body.index("TryParkingSpot(nearest.point, out Vector3 spot)", next_probe)
        new_model = body.index("CreateBike(nearest.point, spot)", placement)
        self.assertLess(retire, guard)
        self.assertLess(guard, reserve)
        self.assertLess(reserve, next_probe)
        self.assertLess(next_probe, placement)
        self.assertLess(placement, new_model)
        self.assertIn("AarisMobileStreamingFrameGate.IsConstrainedDevice()", body[guard:reserve])
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", body[retire:guard])
        self.assertIn("#endif", body[reserve:next_probe])

    def test_deny_only_fresh_instantiation_without_pool_or_victim(self):
        self.assertTrue(defer_new_rig(1, False, False, True))
        self.assertFalse(defer_new_rig(0, False, False, True))
        self.assertFalse(defer_new_rig(1, False, True, True))
        self.assertFalse(defer_new_rig(1, True, False, True))
        self.assertFalse(defer_new_rig(1, False, False, False))
        self.assertIn("parkedBikePool.Count == 0", self.poll)
        self.assertIn("parkedVictim == null", self.poll)
        self.assertIn("ReuseBike(previous, nearest.point, spot)", self.poll)
        self.assertIn("ReuseBike(parkedBikePool.Pop(), nearest.point, spot)", self.poll)

    def test_original_scenery_and_visibility_unchanged(self):
        for token in (
            'ModelResource = "AarisVehicles/Authored/AarisRoadBike"',
            "MaximumVisibleBikes = 3;",
            "LoadRadius = 45f;",
            "UnloadRadius = 66f;",
            "PrewarmRadius = 110f;",
            "parkedBikePool.Push(retired);",
            "Destroy(retired);",
            "retired.SetActive(false);",
            "nearest.nextProbe = now + 3f;",
        ):
            self.assertIn(token, BIKES)
        body = self.poll
        guard = body.index("if (unloaded > 0 && parkedVictim == null &&")
        create = body.index("CreateBike(nearest.point, spot)", guard)
        middle = body[guard:create]
        self.assertNotIn("QualitySettings.", middle)
        self.assertNotIn("renderScale", middle)
        self.assertNotIn("Resources.Load", middle)
        self.assertNotIn("Destroy(", middle)
        self.assertIn("internal static void ReservePhysicsStreamingFrame()", GATE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
