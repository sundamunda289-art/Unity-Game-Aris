#!/usr/bin/env python3
"""Regression contracts for low-RAM vehicle bootstrap prewarming.

Structural checks only. Unity Editor compilation and Android profiling remain
separate acceptance gates. This never changes prefabs or serialized data.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()


class BudgetAndroidVehicleWarmup(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bootstrap = SOURCE.split("internal static void EnsureForPlayer(", 1)[1].split(
            "private bool Setup()", 1
        )[0]
        cls.update = SOURCE.split("private void Update()", 1)[1].split(
            "float now = Time.unscaledTime;", 1
        )[0]

    def test_4gb_or_constrained_gpu_uses_async_boot(self):
        self.assertIn("SystemInfo.systemMemorySize <= 4096", self.bootstrap)
        self.assertIn("AarisMobileRuntime.UseConstrainedGpuStreamingBudget()", self.bootstrap)
        self.assertIn("world.StartCoroutine(world.SetupAfterBudgetAndroidPrewarm(motor))", self.bootstrap)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", self.bootstrap)

    def test_sequential_resource_requests_and_frame_yields(self):
        warmup = self.bootstrap.split(
            "private IEnumerator SetupAfterBudgetAndroidPrewarm(", 1
        )[1]
        self.assertIn('Resources.LoadAsync<TextAsset>("AarisVehicles/vehicle_catalog")', warmup)
        self.assertIn("Resources.LoadAsync<GameObject>(path)", warmup)
        self.assertIn("request.priority = -1;", warmup)
        self.assertIn("yield return request;", warmup)
        self.assertIn("yield return null;", warmup)
        self.assertIn("new HashSet<string>(StringComparer.Ordinal)", warmup)
        self.assertIn("type.modelResource, type.wheelResource", warmup)
        self.assertNotIn("type.authoredModelResource", warmup)
        self.assertNotIn("Instantiate(", warmup)
        self.assertLess(warmup.index("yield return request;"), warmup.index("if (!Setup())"))

    def test_original_validation_and_vehicles_kept(self):
        self.assertIn("if (!world.Setup()) { Destroy(obj); return; }", self.bootstrap)
        self.assertIn("if (!Setup()) { Destroy(gameObject); yield break; }", self.bootstrap)
        self.assertIn("AttachSessionAndHud(motor, this);", self.bootstrap)
        self.assertIn("AttachSessionAndHud(motor, world);", self.bootstrap)
        self.assertIn("private const int TargetVehicles = 44;", SOURCE)
        self.assertIn("preserveAuthoredUrpMaterials", SOURCE)
        self.assertIn("PumpAuthoredBodyLoads();", SOURCE)
        self.assertIn("if (player == null || vehicleTypes == null || prefabs == null) return;", self.update)

    def test_no_serialized_visual_quality_changes(self):
        self.assertIn("using System.Collections;", SOURCE)
        self.assertIn("var hud = hudObject.AddComponent<AarisVehicleHUD>();", SOURCE)
        self.assertIn("var session = motor.GetComponent<AarisVehicleSession>();", SOURCE)
        self.assertIn("prefabs[i] = Resources.Load<GameObject>(item.modelResource);", SOURCE)
        self.assertIn("wheelPrefabs[i] = Resources.Load<GameObject>(item.wheelResource);", SOURCE)


if __name__ == "__main__":
    unittest.main()
