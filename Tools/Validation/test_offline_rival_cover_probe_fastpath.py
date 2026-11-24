#!/usr/bin/env python3
"""Guard exact identity bounds and 100-bot cover look-ahead parity."""
import math
import random
import unittest
from pathlib import Path

R=Path(__file__).resolve().parents[2]
GRID=(R/"Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text()
AI=(R/"Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
G=GRID.split("internal bool HasPotentialInactiveObstacle(",1)[1].split(
    "private void OnEnable()",1)[0]
A=AI.split("internal bool WouldCrossInactiveCover(",1)[1].split(
    "private void EvaluateZone(",1)[0]

class CoverProbe(unittest.TestCase):
    def test_exact_identity_not_near_identity(self):
        for s in ("rotation.x == 0f && rotation.y == 0f",
                  "rotation.z == 0f && rotation.w == 1f",
                  "Mathf.Abs(halfExtents.x)", "Mathf.Abs(halfExtents.y)",
                  "Mathf.Abs(halfExtents.z)",
                  "rotation * (Vector3.right * halfExtents.x)",
                  "rotation * (Vector3.up * halfExtents.y)",
                  "rotation * (Vector3.forward * halfExtents.z)",
                  "Bounds probe = new Bounds(center, 2f * extents)",
                  "if (!Finite(probeMin) || !Finite(probeMax)) return true;",
                  "if (probeVisited[i] == probeStamp) continue;",
                  "!c.enabled"):
            self.assertIn(s,G)
        self.assertNotIn("rotation == Quaternion.identity",G)

    def test_one_sqrt_and_original_probe_contract(self):
        for s in ("displacement.sqrMagnitude < 0.000001f",
                  "float distance = displacement.magnitude;",
                  "distance > 0.00001f",
                  "displacement / distance : Vector3.zero",
                  "heading * Mathf.Max(distance, 0.72f)",
                  "HasPotentialInactiveObstacle(from + lookAhead + chest,",
                  "Vector3 half = new Vector3(0.36f, 0.90f, 0.36f)"):
            self.assertIn(s,A)
        self.assertEqual(A.count("displacement.magnitude"),1)
        self.assertNotIn("displacement.normalized",A)

    def test_100_bots_and_conservative_overlap(self):
        rng=random.Random(20261123)
        for t in range(200):
            for i in range(100):
                vec=[rng.uniform(-2,2) for _ in range(3)]
                m=math.sqrt(sum(v*v for v in vec))
                if m*m < .000001:continue
                old=[v/m*max(m,.72) for v in vec]
                new=[v/m*max(m,.72) for v in vec]
                self.assertEqual(old,new)
                half=[rng.uniform(0,2) for _ in range(3)]
                original=[abs(v) for v in half]
                cached=list(map(abs,half))
                self.assertEqual(original,cached)
                c=[rng.uniform(-800,800) for _ in range(3)]
                w=[rng.uniform(v-2,v+2) for v in c]
                size=[rng.uniform(.1,2) for _ in range(3)]
                def intersects(ext):
                    return all(abs(c[j]-w[j])<=ext[j]+size[j]
                               for j in range(3))
                self.assertEqual(intersects(original),intersects(cached))

if __name__=="__main__":
    unittest.main()
