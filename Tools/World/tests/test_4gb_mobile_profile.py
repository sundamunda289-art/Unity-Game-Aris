"""Static regression for Android 4 GB-class mobile profile inclusion.

Does not compile Unity or measure phone FPS.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
RUNTIME = ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs"


class FourGbMobileProfileContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = RUNTIME.read_text(encoding="utf-8")

    def test_4gb_phones_no_longer_skip_all_adaptive_budgets(self):
        s = self.source
        match = re.search(r"if \(systemRamMb <= 0 \|\| systemRamMb > (\d+)\) return;", s)
        self.assertIsNotNone(match, "RAM gate must remain explicit and reviewable")
        cap = int(match.group(1))
        self.assertEqual(cap, 4096)
        self.assertLessEqual(3760, cap, "3.7 GiB-class test phone must be eligible")
        self.assertGreater(5120, cap, "5 GB-class devices must not be changed")

    def test_2gb_and_3gb_keep_existing_budgets(self):
        s = self.source
        self.assertIn("bool constrained = systemRamMb <= 2304;", s)
        self.assertIn("constrained ? 256f : 320f", s)
        self.assertIn("constrained ? 55f : 80f", s)
        self.assertIn("constrained ? 0.58f : 0.78f", s)
        self.assertIn('systemRamMb <= 3584 ? "ram-3gb" : "ram-4gb"', s)
        self.assertIn('mobileProfile = constrained ? "ram-2gb"', s)

    def test_4gb_budget_retains_horizon_and_recovers_detail(self):
        s = self.source
        self.assertIn("terrain.detailObjectDistance = originalDetailDistances[i];", s)
        self.assertIn("terrain.detailObjectDensity = originalDetailDensities[i];", s)
        self.assertIn("terrain.treeMaximumFullLODCount = originalFullLodTreeCounts[i];", s)
        self.assertIn("mobilePipeline.renderScale = originalRenderScale;", s)
        self.assertIn("if (reason == \"sample\") TuneForFrameTime(s.p95FrameMs);", s)
        self.assertNotIn("terrain.treeDistance =", s)
        self.assertNotIn("farClipPlane =", s)
        self.assertNotIn("QualitySettings.lodBias =", s)


if __name__ == "__main__":
    unittest.main()
