"""Source-only regression for precise, read-only suspicious house face diagnostics."""
import sys
import unittest
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT / "Tools" / "World"))
from diagnose_suspicious_house_faces import face_metrics


class HouseTriangleMeasurements(unittest.TestCase):
    def test_thin_long_face_is_identified_by_true_altitude(self):
        triangles=np.array([
            [[0,0,0],[10,0,0],[10,0.001,0]],
            [[0,0,0],[1,0,0],[0.5,0.8660254,0]],
        ],dtype=np.float64)
        longest, altitude, aspect=face_metrics(triangles)
        self.assertAlmostEqual(float(longest[0]),10,places=3)
        self.assertAlmostEqual(float(altitude[0]),0.001,places=5)
        self.assertGreater(float(aspect[0]),9000)
        self.assertLess(float(aspect[1]),1.2)

    def test_refuses_empty_or_nonfinite_geometry(self):
        with self.assertRaises(ValueError):
            face_metrics(np.empty((0,3,3),dtype=float))
        with self.assertRaises(ValueError):
            face_metrics(np.array([[[0,0,0],[1,0,0],[float("nan"),0,0]]]))


if __name__ == "__main__":
    unittest.main()
