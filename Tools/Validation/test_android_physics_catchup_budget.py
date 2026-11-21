#!/usr/bin/env python3
"""Static and arithmetic regression for bounded low-end Android physics catch-up."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text()
TIME = (ROOT / "ProjectSettings/TimeManager.asset").read_text()

class AndroidFixedStepBudget(unittest.TestCase):
    def test_applies_to_budget_android_gpus_independent_of_ram_class(self):
        awake = SRC.split("private void Awake()", 1)[1].split("private IEnumerator Start()", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", awake)
        self.assertIn("int ramMB = SystemInfo.systemMemorySize;", awake)
        self.assertIn("if (ramMB > 0 && IsBudgetAndroidGpu())", awake)
        self.assertNotIn("ramMB <= 4608", awake)
        self.assertNotIn("ramMB <= 4096", awake)
        self.assertIn("Time.fixedDeltaTime * 6f", awake)
        self.assertIn("Time.maximumDeltaTime = appliedMaximumDeltaTime;", awake)
        self.assertIn("Mathf.Min(", awake)
    def test_keeps_native_physics_frequency_and_visuals(self):
        self.assertIn("Maximum Allowed Timestep: 0.33333334", TIME)
        self.assertIn("m_Numerator: 141120000", TIME)
        self.assertIn("m_Count: 2822399", TIME)
        self.assertNotIn("Time.fixedDeltaTime =", SRC)
        self.assertIn("private void ConfigureTerrainInstanceRenderer()", SRC)
    def test_restores_only_own_global_time_value(self):
        teardown = SRC.split("private void OnDestroy()", 1)[1]
        self.assertIn("if (modifiedMaximumDeltaTime &&", teardown)
        self.assertIn("Mathf.Approximately(Time.maximumDeltaTime, appliedMaximumDeltaTime)", teardown)
        self.assertIn("Time.maximumDeltaTime = originalMaximumDeltaTime;", teardown)
    def test_records_device_observable_limit(self):
        self.assertIn("public float fixedPhysicsStepMs, maxPhysicsCatchupMs;", SRC)
        self.assertIn("maxPhysicsCatchupMs = Time.maximumDeltaTime * 1000f", SRC)
    def test_six_steps_not_sixteen_for_20ms_physics(self):
        timestep = 2822399 / 141120000
        self.assertAlmostEqual(timestep, .02, places=6)
        self.assertGreater((.33333334 / timestep), 16)
        self.assertAlmostEqual((timestep * 6) / timestep, 6)
if __name__ == "__main__":
    unittest.main(verbosity=2)
