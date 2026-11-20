#!/usr/bin/env python3
"""Android 4 GB vehicle-stream budget regression contracts.

These source checks do not substitute for Unity compilation, Android
Profiler captures, or on-device crash/low-memory testing.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets" / "AarisWorld" / "Scripts"
MOBILE = (SCRIPTS / "AarisMobileRuntime.cs").read_text(encoding="utf-8")
VEHICLES = (SCRIPTS / "AarisVehicleWorld.cs").read_text(encoding="utf-8")


def budget_for_device(memory_mb, budget_gpu):
    # Mirrors the one-time Awake policy: RAM and GPU are different limits.
    return (memory_mb > 0 and memory_mb <= 3072) or budget_gpu


class AndroidFourGigVehicleBudget(unittest.TestCase):
    def test_budget_gpu_reuses_exact_runtime_detector(self):
        self.assertIn("private static bool IsBudgetAndroidGpu()", MOBILE)
        self.assertIn("internal static bool UseConstrainedGpuStreamingBudget()", MOBILE)
        helper = MOBILE.split(
            "internal static bool UseConstrainedGpuStreamingBudget()", 1
        )[1].split("// Model prefixes are not interchangeable:", 1)[0]
        self.assertIn("return IsBudgetAndroidGpu();", helper)
        self.assertNotIn("SystemInfo.systemMemorySize", helper)
        self.assertLess(
            MOBILE.index("#if UNITY_ANDROID && !UNITY_EDITOR\n        // Runs once after URP"),
            MOBILE.index("internal static bool UseConstrainedGpuStreamingBudget()"),
        )

    def test_vehicle_budget_is_android_only_and_capped(self):
        awake = VEHICLES.split("private void Awake()", 1)[1].split(
            "[RuntimeInitializeOnLoadMethod", 1
        )[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", awake)
        self.assertIn(
            "if ((memoryMB > 0 && memoryMB <= 3072) ||\n"
            "                AarisMobileRuntime.UseConstrainedGpuStreamingBudget())",
            awake,
        )
        for assignment in (
            "spawnBudgetPerPoll = 1;",
            "unloadBudgetPerPoll = 1;",
            "spawnProbeBudgetPerPoll = 3;",
            "maxConcurrentAuthoredLoads = 1;",
        ):
            self.assertIn(assignment, awake)
        self.assertIn("#endif", awake)

    def test_4_gb_budget_gpu_gets_low_pressure_path(self):
        self.assertTrue(budget_for_device(4096, True))
        self.assertTrue(budget_for_device(6144, True))
        self.assertTrue(budget_for_device(3072, False))
        self.assertFalse(budget_for_device(4096, False))
        self.assertFalse(budget_for_device(8192, False))
        self.assertFalse(budget_for_device(0, False))

    def test_original_fidelity_and_stronger_device_policy_preserved(self):
        # Reduces bursts, never vehicle count, draw radius or source quality.
        for text in (
            "private const int TargetVehicles = 44;",
            "private const int MaximumNearbyVehicles = 10;",
            "private const float LoadRadius = 105f;",
            "private int spawnBudgetPerPoll = 2;",
            "private int unloadBudgetPerPoll = 4;",
            "private int spawnProbeBudgetPerPoll = 8;",
            "private int maxConcurrentAuthoredLoads = 2;",
        ):
            self.assertIn(text, VEHICLES)


if __name__ == "__main__":
    unittest.main(verbosity=2)
