#!/usr/bin/env python3
"""Regression contracts for budget Android GPU-name aliases.

Source-level checks only: actual GPU timings and FPS require an Android device.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text(
    encoding="utf-8"
)
DETECTOR = SOURCE.split("private static bool IsBudgetAndroidGpu()", 1)[1].split(
    "// One-time, Android-only vegetation work", 1
)[0]


def simulated_budget_gpu(name):
    """Mirror the one-time, narrow string matching in the runtime policy."""
    normalized = name.replace("-", " ").replace("_", " ").lower()
    def exact_model(model):
        start = 0
        while start <= len(normalized) - len(model):
            index = normalized.find(model, start)
            if index < 0:
                return False
            end = index + len(model)
            if ((index == 0 or not normalized[index - 1].isalnum()) and
                    (end == len(normalized) or not normalized[end].isalnum())):
                return True
            start = index + 1
        return False

    g57 = any(exact_model("mali g57 " + suffix) for suffix
              in ("mc1", "mc2", "mp1", "mp2"))
    mali = any(exact_model(x) for x in ("mali g52", "mali g51", "mali g31"))
    adreno = any(exact_model(x) for x in ("adreno 610", "adreno (tm) 610"))
    powervr = any(exact_model(x) for x
                  in ("powervr ge8320", "powervr rogue ge8320"))
    return g57 or mali or adreno or powervr


class BudgetGpuNameAliases(unittest.TestCase):
    def test_normalized_legacy_mali_and_exact_adreno_aliases_in_source(self):
        self.assertIn(
            "string normalizedGpu = gpu.Replace('-', ' ').Replace('_', ' ');",
            DETECTOR,
        )
        for model in ("Mali G52", "Mali G51", "Mali G31"):
            self.assertIn('HasExactGpuModel(normalizedGpu, "' + model + '")', DETECTOR)
        self.assertIn('HasExactGpuModel(gpu, "Adreno (TM) 610")', DETECTOR)
        self.assertIn('HasExactGpuModel(gpu, "PowerVR Rogue GE8320")', DETECTOR)
        self.assertIn("return smallG57 || legacyMali || smallPowerVr", DETECTOR)
        self.assertIn('HasExactGpuModel(normalizedGpu, "Mali G57 MC2")', DETECTOR)
        self.assertIn("!char.IsLetterOrDigit(description[end])", SOURCE)

    def test_equivalent_low_end_gpu_names(self):
        names = (
            "Mali-G52", "Mali G52 MC2", "Mali_G52", "MALI-G51",
            "Mali G31", "Mali_G31", "Mali-G57 MC2", "Mali G57 MP1",
            "Adreno 610", "Adreno (TM) 610", "PowerVR GE8320",
            "PowerVR Rogue GE8320",
        )
        for name in names:
            with self.subTest(name=name):
                self.assertTrue(simulated_budget_gpu(name))

    def test_stronger_and_unknown_gpus_keep_native_budget(self):
        names = (
            "Mali-G57 MC3", "Mali-G57 MC4", "Mali-G76 MC4",
            "Adreno 618", "Adreno 730", "Mali G610 MC6",
            "PowerVR GE8300", "Mali-G520 MC2", "Mali G510",
            "Mali G570 MC2", "Mali G310", "unknown", "",
            "Mali G57 MC21", "Mali G57 MP21", "Mali G57 MC4 driver MC2",
            "Adreno 6100", "Adreno (TM) 610X", "PowerVR GE83200",
            "PowerVR Rogue GE8320X",
        )
        for name in names:
            with self.subTest(name=name):
                self.assertFalse(simulated_budget_gpu(name))

    def test_prefix_collision_before_later_exact_model(self):
        self.assertTrue(simulated_budget_gpu("Mali G520 / Mali G52 MC2"))
        self.assertTrue(simulated_budget_gpu("Adreno 6100; Adreno 610"))
        self.assertTrue(simulated_budget_gpu("PowerVR GE83200 / PowerVR GE8320"))

    def test_quality_path_remains_runtime_only_and_reversible(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", SOURCE)
        self.assertIn("bool constrainedGraphics = memoryMB <= 2304 || IsBudgetAndroidGpu();", SOURCE)
        self.assertIn("if (constrainedGraphics)", SOURCE)
        self.assertIn("ConfigureLowMemoryVegetation();", SOURCE)
        self.assertIn("if (modifiedShadowDistance && androidPipeline != null", SOURCE)
        self.assertIn("if (modifiedMsaaSamples && androidPipeline != null", SOURCE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
