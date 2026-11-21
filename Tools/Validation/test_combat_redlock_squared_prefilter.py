#!/usr/bin/env python3
"""Red-Lock cone optimization regression; source/math checks, not PlayMode."""
from pathlib import Path
import math
import random
import unittest

BOTS = (Path(__file__).resolve().parents[2] /
        "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()


class RedLockSquaredPrefilterTests(unittest.TestCase):
    def test_cheap_gate_before_sqrt_keeps_cover_authority(self):
        src = BOTS.split("internal static bool TryAcquireAimTarget", 1)[1]
        src = src.split("private ", 1)[0]
        for token in ("maximumRangeSqr = range * range",
                      "sqrDistance = delta.sqrMagnitude",
                      "projected * projected <= threshold * threshold * sqrDistance",
                      "Mathf.Sqrt(sqrDistance)",
                      "closest.transform.IsChildOf(candidate.transform)",
                      "for (int attempt = 0; attempt < 3; attempt++)"):
            self.assertIn(token, src)
        self.assertLess(src.index("projected * projected <="),
                        src.index("Mathf.Sqrt(sqrDistance)"))
        self.assertNotIn("delta.magnitude", src)

    def test_same_candidates_for_all_scope_cones(self):
        rng = random.Random(112211)
        for cone in (1.4, 2.3, 3.7):
            threshold = math.cos(math.radians(cone))
            for _ in range(12000):
                dx, dy, dz = (rng.uniform(-200, 200) for _ in range(3))
                sqr = dx*dx + dy*dy + dz*dz
                d = math.sqrt(sqr)
                legacy = 1 <= d <= 160 and dz/d > threshold
                fast = 1 <= sqr <= 160**2 and dz > 0 and (
                    dz*dz > threshold*threshold*sqr)
                self.assertEqual(fast, legacy)

    def test_range_and_view_boundaries(self):
        for cone in (1.4, 2.3, 3.7):
            c = math.cos(math.radians(cone))
            for angle in (0, cone/2, cone*1.15, 90, 179):
                for length in (.75, 1.25, 80, 160, 170):
                    projection = length*math.cos(math.radians(angle))
                    sqr = length*length
                    fast = (1 <= sqr <= 160**2 and projection > 0 and
                            projection*projection > c*c*sqr)
                    self.assertEqual(fast, 1 <= length <= 160 and angle < cone)


if __name__ == "__main__":
    unittest.main()
