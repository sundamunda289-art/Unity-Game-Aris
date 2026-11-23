#!/usr/bin/env python3
"""Signed 110m cell/index parity under 100 moving and teleported rivals."""
import math
import random
import unittest
from pathlib import Path

SRC = (Path(__file__).resolve().parents[2] /
       "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
INDEX = SRC.split("internal void TrackRivalPosition(", 1)[1].split(
    "private void BootstrapRivalSpatialIndex(", 1)[0]

def cell(x):
    return math.floor(x / 110.)

class Slot:
    def __init__(self):
        self.key = None
        self.lo = self.hi = None
        self.changes = 0
    def move(self, x, z):
        if self.key is not None:
            if (self.lo[0]+.01 <= x < self.hi[0]-.01 and
                self.lo[1]+.01 <= z < self.hi[1]-.01):
                return
        key = cell(x), cell(z)
        if key == self.key:
            return
        self.key = key
        self.lo = key[0]*110., key[1]*110.
        self.hi = (key[0]+1)*110., (key[1]+1)*110.
        self.changes += 1

class SpatialFastpath(unittest.TestCase):
    def test_source_position_cache_and_boundary_fallback(self):
        for s in ("rivalSpatialPositions[slot] = position;",
                  "private readonly Vector2[] rivalCellMin",
                  "private readonly Vector2[] rivalCellMax",
                  "private const float RivalCellBoundaryGuard = 0.01f;",
                  "position.x >= low.x + RivalCellBoundaryGuard",
                  "position.z >= low.y + RivalCellBoundaryGuard",
                  "rivalCellMin[slot] = new Vector2(x * RivalHuntRadius,",
                  "rivalCellMax[slot] = new Vector2((x + 1f) * RivalHuntRadius,"):
            self.assertIn(s, SRC)
        self.assertLess(INDEX.index("rivalSpatialPositions[slot] = position;"),
                        INDEX.index("if (rivalSpatialRegistered[slot])"))
        self.assertLess(INDEX.index("position.x >= low.x"),
                        INDEX.index("int x = RivalCell(position.x)"))
        self.assertLess(INDEX.index("rivalCellMax[slot] ="),
                        INDEX.index("rivalSpatialRegistered[slot] = true;"))

    def test_100_rivals_movement_teleport_parity(self):
        rng = random.Random(20261123)
        slots = [Slot() for _ in range(100)]
        positions = [(rng.uniform(-980,980),rng.uniform(-980,980))
                     for _ in slots]
        for frame in range(1200):
            for i, slot in enumerate(slots):
                x,z=positions[i]
                if frame%197==0:
                    x,z=rng.uniform(-990,990),rng.uniform(-990,990)
                else:
                    x+=rng.uniform(-1.6,1.6)
                    z+=rng.uniform(-1.6,1.6)
                positions[i]=(x,z)
                slot.move(x,z)
                self.assertEqual(slot.key,(cell(x),cell(z)))
        self.assertLess(sum(s.changes for s in slots),11000)

    def test_negative_cells_and_110m_seams(self):
        for n in range(-9,10):
            s=Slot()
            for d in (-.02,-.01,-.0001,0.,.0001,.01,.02,-151.,151.):
                x,z=n*110.+d,-n*110.-d
                s.move(x,z)
                self.assertEqual(s.key,(cell(x),cell(z)))
        s=Slot()
        s.move(-.001,-110.001)
        self.assertEqual(s.key,(-1,-2))
        s.move(0.,-110.)
        self.assertEqual(s.key,(0,-1))

if __name__=="__main__":
    unittest.main()
