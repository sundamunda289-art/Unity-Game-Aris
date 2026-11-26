#!/usr/bin/env python3
"""86m attacker fast reject: source contract and deterministic 100-rival parity."""
from pathlib import Path
import math
import random
import unittest
SRC=(Path(__file__).resolve().parents[2]/"Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
def cell(x): return math.floor(x/110.)
def bucket(x,z): return ((x*73856093)^(z*19349663))&255
def indexed_near(player,bots):
    buckets=[[] for _ in range(256)]
    for x,z,visible in bots:
        if visible: buckets[bucket(cell(x),cell(z))].append((x,z))
    cx,cz=cell(player[0]),cell(player[1])
    for x in range(cx-1,cx+2):
        for z in range(cz-1,cz+2):
            for bx,bz in buckets[bucket(x,z)]:
                if cell(bx)==x and cell(bz)==z and (bx-player[0])**2+(bz-player[1])**2<86**2:
                    return True
    return False
class TestAttackPrefilter(unittest.TestCase):
    def test_source_preserves_two_attacker_physical_selection(self):
        check=SRC.split("private bool HasPotentialPlayerAttacker()",1)[1].split("private void RefreshPlayerAttackers()",1)[0]
        scan=SRC.split("private void RefreshPlayerAttackers()",1)[1].split("internal bool MayAttackPlayer(",1)[0]
        for key in ("BootstrapRivalSpatialIndex()", "RivalCellKey(x, z)", "rivalSpatialKeys[i] != key","rival.HasVisibleBody","AarisOfflineRival.MaximumSightRange"):
            self.assertIn(key,check)
        for key in ("if (!HasPotentialPlayerAttacker())","attackerOne = attackerTwo = null","AttackerSelectionPerFrame","bot.HasPlayerSightForSelection()"):
            self.assertIn(key,scan)
    def test_100_opponents_bounds_and_negative_cells(self):
        rng=random.Random(20261123)
        for _ in range(140):
            player=(rng.uniform(-1100,1100),rng.uniform(-1100,1100))
            bots=[(rng.uniform(-1200,1200),rng.uniform(-1200,1200),bool(rng.getrandbits(1))) for i in range(100)]
            reference=any(v and (x-player[0])**2+(z-player[1])**2<86**2 for x,z,v in bots)
            self.assertEqual(indexed_near(player,bots),reference)
        for p in ((0,0),(-110,-110),(110,-220),(-220,110)):
            bots=[(900,900,True)]*99+[(p[0]+85.9,p[1],True)]
            self.assertTrue(indexed_near(p,bots))
            bots[-1]=(p[0]+86,p[1],True)
            self.assertFalse(indexed_near(p,bots))
            bots[-1]=(p[0]+1,p[1],False)
            self.assertFalse(indexed_near(p,bots))
if __name__=="__main__": unittest.main()
