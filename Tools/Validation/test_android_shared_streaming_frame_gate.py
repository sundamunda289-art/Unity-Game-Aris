#!/usr/bin/env python3
"""Shared optional-model integration admission: Android source regressions."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text()
CARS = (SRC / "AarisVehicleWorld.cs").read_text()
LOOT = (SRC / "AarisWeaponLootWorld.cs").read_text()
BIKE = (SRC / "AarisRoadsideBikeScenery.cs").read_text()
CLAIM = "AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()"


def simulate_admission(available_ram, frame_numbers, budget_gpu=False):
    """Python control-flow mirror, NOT a Unity runtime measurement."""
    last = -1
    decisions = []
    for frame in frame_numbers:
        if (available_ram <= 0 or available_ram > 4608) and not budget_gpu:
            decisions.append(True)
        elif frame == last:
            decisions.append(False)
        else:
            last = frame
            decisions.append(True)
    return decisions


class SharedStreamingAdmission(unittest.TestCase):
    def test_low_ram_or_budget_gpu_android_only(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", GATE)
        self.assertIn("constrainedDevice = (ramMb > 0 && ramMb <= 4608) ||", GATE)
        self.assertIn("AarisMobileRuntime.UseConstrainedGpuStreamingBudget();", GATE)
        self.assertIn("int frame = Time.frameCount;", GATE)
        self.assertRegex(GATE,
                         r"if \(claimedFrame == frame\)\s*\{\s*"
                         r"SameFrameDeferredAttempts\+\+;\s*return false;\s*\}")
        self.assertIn("claimedFrame = frame;", GATE)
        self.assertEqual(simulate_admission(4096, [1, 1, 2, 2, 3]),
                         [True, False, True, False, True])
        for ram in (0, -1, 6144, 8192):
            self.assertEqual(simulate_admission(ram, [1, 1, 1]),
                             [True, True, True])
            self.assertEqual(simulate_admission(ram, [1, 1, 2], budget_gpu=True),
                             [True, False, True])

    def test_reset_clears_per_scene_static_slot(self):
        self.assertIn("[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]", GATE)
        self.assertIn("private static void Reset()", GATE)
        self.assertIn("claimedFrame = -1;", GATE)
        self.assertTrue((SRC / "AarisMobileStreamingFrameGate.cs.meta").is_file())

    def test_vehicle_slot_is_claimed_before_build_and_before_eviction(self):
        body = CARS.split("private void Update()", 1)[1].split(
            "private AarisVehicleRecord FarthestReplaceableParkedCar(", 1)[0]
        self.assertIn("if (!" + CLAIM + ")", body)
        self.assertIn("record.nextProbe = now + .15f;", body)
        self.assertLess(body.index(CLAIM), body.index("if (!Build(record, pos))"))
        self.assertLess(body.index(CLAIM), body.index("Destroy(retired.gameObject);"))
        self.assertIn("(parkedVictim.visible == null || !parkedVictim.visible.CanPark)", body)
        self.assertIn("if (parkedVictim != null)", body)

    def test_loot_consults_shared_slot_before_both_optional_models(self):
        body = LOOT.split("private bool StreamNearby(", 1)[1].split(
            "private void EnsureStarterLoot(", 1)[0]
        self.assertEqual(body.count(CLAIM), 2)
        self.assertLess(body.index(CLAIM), body.index('new GameObject("Dropped "'))
        self.assertLess(body.rindex(CLAIM), body.index('new GameObject("Loot "'))
        self.assertEqual(body.count("if (!" + CLAIM + ") return true;"), 2)
        self.assertIn("if (buildAttempts >= modelBuildBudget)", body)

    def test_starter_loot_not_delayed_by_optional_slot(self):
        starter = LOOT.split("private void EnsureStarterLoot(", 1)[1]
        self.assertNotIn(CLAIM, starter.split("private ", 1)[0])
        self.assertIn("starterLootSettled", starter)
        self.assertIn("TryFindDropSpot", starter)

    def test_bike_prewarm_materials_and_new_rigs_share_slot(self):
        body = BIKE.split("private void Update()", 1)[1].split(
            "private GameObject ReuseBike(", 1)[0]
        self.assertEqual(body.count(CLAIM), 2)
        self.assertLess(body.index("if (!bikePrewarm.isDone) return;"),
                        body.index(CLAIM))
        self.assertLess(body.index(CLAIM), body.index("SetupMaterials();"))
        self.assertLess(body.index("TryParkingSpot(nearest.point, out Vector3 spot)"),
                        body.rindex(CLAIM))
        self.assertIn("parkedVictim == null && parkedBikePool.Count == 0", body)
        self.assertIn("nearest.nextProbe = now + .4f;", body)

    def test_bike_pool_reuse_not_blocked_or_destroyed(self):
        body = BIKE.split("private void Update()", 1)[1].split(
            "private GameObject ReuseBike(", 1)[0]
        self.assertIn("ReuseBike(previous, nearest.point, spot)", body)
        self.assertIn("ReuseBike(parkedBikePool.Pop(), nearest.point, spot)", body)
        self.assertIn("CreateBike(nearest.point, spot)", body)
        self.assertIn("parkedVictim == null && parkedBikePool.Count == 0", body)
        self.assertNotIn(CLAIM, BIKE.split("private GameObject ReuseBike(", 1)[1].
                         split("private bool TryParkingSpot(", 1)[0])

    def test_authoritative_controls_visuals_preserved(self):
        self.assertIn("private const int MaximumNearbyVehicles = 10;", CARS)
        self.assertIn("private const int MaximumBuildAttemptsPerFrame = 2;", LOOT)
        self.assertIn("private const int MaximumVisibleBikes = 3;", BIKE)
        for code in (CARS, LOOT, BIKE):
            self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", code)

    def test_bounded_conflict_resolves_next_frame(self):
        # If vehicle wins frame N, a loot/bike candidate is free in N+1.
        for label in ("vehicle", "loot", "bike"):
            decisions = simulate_admission(4096, [19, 19, 20, 20, 21])
            self.assertEqual(decisions, [True, False, True, False, True], label)


if __name__ == "__main__":
    unittest.main(verbosity=2)
