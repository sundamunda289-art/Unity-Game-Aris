"""Source-level and analytic regression for the SpeedTree v3 mid-trunk taper.

No Unity executable required; these are mathematical/source contracts only.
The real imported meshes, silhouette, collision and FPS need Editor review.
"""
from __future__ import annotations

import json
import math
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[3]
IMPORTER = ROOT / "Assets/AarisWorld/Editor/AarisTreeShapeImporter.cs"
MANIFEST = ROOT / "Tools/World/tree-realism-v2-reviewed.json"


def profile(u: float, lower: float, upper: float):
    u = max(0.0, min(1.0, u))
    smooth = u * u * (3.0 - 2.0 * u)
    contrast = max(0.0, min(1.0, lower / upper - 1.0))
    bias = 0.35 + 0.25 * contrast
    shaped = smooth - bias * smooth * (1.0 - smooth)
    radial = lower + (upper - lower) * shaped
    derivative = (upper - lower) * (1.0 - bias + 2.0 * bias * smooth) * 6.0 * u * (1.0 - u)
    return radial, derivative


class SpeedTreeTaperV3Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.manifest = json.loads(MANIFEST.read_text())
        cls.importer = IMPORTER.read_text()

    def test_importer_uses_one_profile_for_mesh_and_morph_anchors(self):
        self.assertEqual(self.manifest["importer_version"], 3)
        self.assertIn("public override uint GetVersion() { return 3; }", self.importer)
        self.assertIn("private static void RadialProfile(", self.importer)
        self.assertIn("RadialProfile(p.y, height, lower, upper, out float radial, out _);", self.importer)
        self.assertIn("RadialProfile(p.y, height, lower, upper, out float radial, out float derivative);", self.importer)
        self.assertIn("WarpPoint(new Vector3(m.x, m.y, m.z)", self.importer)
        self.assertIn("WarpPoint(new Vector3(a.z, a.w, m.w)", self.importer)
        self.assertIn("6f * u * (1f - u) / height", self.importer)

    def test_six_reviewed_species_keep_bounds_and_fuller_mid_trunks(self):
        self.assertEqual(len(self.manifest["profiles"]), 6)
        widths = set()
        for row in self.manifest["profiles"]:
            h = row["height_factor"]
            lower, upper = row["base_width_factor"] / h, row["crown_width_factor"] / h
            self.assertGreater(lower, upper)
            widths.add((row["base_width_factor"], row["crown_width_factor"]))
            self.assertAlmostEqual(profile(0, lower, upper)[0], lower)
            self.assertAlmostEqual(profile(1, lower, upper)[0], upper)
            previous = lower
            for i in range(1, 1001):
                u = i / 1000
                value, _ = profile(u, lower, upper)
                old = lower + (upper - lower) * (u * u * (3 - 2 * u))
                self.assertLessEqual(value, previous + 1e-11)
                self.assertGreaterEqual(value, upper - 1e-11)
                self.assertLessEqual(value, lower + 1e-11)
                self.assertGreaterEqual(value + 1e-11, old)
                self.assertTrue(math.isfinite(value))
                previous = value
            mid = profile(0.5, lower, upper)[0]
            old_mid = (lower + upper) / 2
            self.assertGreater(mid, old_mid * 1.02)
        self.assertGreaterEqual(len(widths), 4)  # Natural species-level variation preserved

    def test_analytic_derivative_matches_numerical_differentiation(self):
        for row in self.manifest["profiles"]:
            h = row["height_factor"]
            lower, upper = row["base_width_factor"] / h, row["crown_width_factor"] / h
            for u in (0.02, 0.1, 0.25, 0.5, 0.75, 0.95, 0.99):
                eps = 1e-5
                numerical = (profile(u + eps, lower, upper)[0] - profile(u - eps, lower, upper)[0]) / (2 * eps)
                actual = profile(u, lower, upper)[1]
                self.assertAlmostEqual(actual, numerical, delta=1e-6)
                self.assertLess(actual, 0)
            self.assertAlmostEqual(profile(0, lower, upper)[1], 0)
            self.assertAlmostEqual(profile(1, lower, upper)[1], 0)


if __name__ == "__main__":
    unittest.main()
