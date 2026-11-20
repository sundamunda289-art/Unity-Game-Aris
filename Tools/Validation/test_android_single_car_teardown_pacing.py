#!/usr/bin/env python3
"""Budget-device parked-car teardown regression, source + deterministic policy.

These tests cannot measure Unity native Destroy cost or Android FPS.
"""
import unittest
from pathlib import Path

SRC = (Path(__file__).resolve().parents[2] /
       "Assets/AarisWorld/Scripts")
VEHICLES = (SRC / "AarisVehicleWorld.cs").read_text(encoding="utf-8")
MOBILE = (SRC / "AarisMobileRuntime.cs").read_text(encoding="utf-8")


def retirement_polls(cars, maximum_per_poll):
    """Mirror the eligibility limit: occupied/rolling vehicles never retire."""
    remaining = list(cars)
    history = []
    while any(remaining):
        count = 0
        for i, eligible in enumerate(remaining):
            if eligible and count < maximum_per_poll:
                remaining[i] = False
                count += 1
        history.append(count)
        if len(history) > len(cars) + 1:
            raise AssertionError("retirement scheduler never drained")
    return history


class OneCarNativeTeardownBudget(unittest.TestCase):
    def test_adreno_gpu_detected_without_ram_dependence(self):
        self.assertIn('HasExactGpuModel(gpu, "Adreno 610")', MOBILE)
        self.assertIn('HasExactGpuModel(gpu, "Adreno (TM) 610")', MOBILE)
        awake = VEHICLES.split("private void Awake()", 1)[1].split(
            "[RuntimeInitializeOnLoadMethod", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", awake)
        self.assertIn("AarisMobileRuntime.UseConstrainedGpuStreamingBudget()", awake)
        self.assertIn("unloadBudgetPerPoll = 1;", awake)

    def test_original_44_car_plan_visible_count_and_distances_untouched(self):
        for marker in (
            "private const int TargetVehicles = 44;",
            "private const int MaximumNearbyVehicles = 10;",
            "private const float LoadRadius = 105f;",
            "private const float UnloadRadius = 160f;",
            "private int unloadBudgetPerPoll = 4;",
        ):
            self.assertIn(marker, VEHICLES)
        update = VEHICLES.split("private void Update()", 1)[1]
        self.assertIn("unloaded < unloadBudgetPerPoll", update)
        self.assertIn("dist2 > UnloadRadius * UnloadRadius && record.visible.CanPark", update)

    def test_one_retired_per_poll_eventually_drains_all_far_parked_cars(self):
        # All four eligible cars retire on successive polls, never in one.
        self.assertEqual(retirement_polls([True, True, True, True], 1), [1, 1, 1, 1])
        self.assertEqual(retirement_polls([True], 1), [1])
        self.assertEqual(retirement_polls([], 1), [])

    def test_rolling_and_occupied_cars_are_not_even_candidates(self):
        # Represent only CanPark=true distant cars as eligible.
        self.assertEqual(retirement_polls([True, False, False, True], 1), [1, 1])
        update = VEHICLES.split("private void Update()", 1)[1]
        self.assertLess(update.index("record.damage01 = record.visible.Damage01;"),
                        update.index("Destroy(record.visible.gameObject);"))
        self.assertIn("if (unloaded > 0 && AarisMobileStreamingFrameGate.IsConstrainedDevice())", update)


if __name__ == "__main__":
    unittest.main(verbosity=2)
