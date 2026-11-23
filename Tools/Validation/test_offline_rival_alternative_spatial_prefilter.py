#!/usr/bin/env python3
"""Conservative dormant-only alternative-LOS prefilter for 100-bot rosters."""
import random
import unittest
from pathlib import Path

SRC=(Path(__file__).resolve().parents[2]/
     "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
SCAN=SRC.split("internal bool TryFindVisibleRivalAlternative(",1)[1].split(
    "// A camera-visible enemy is not red-locked",1)[0]

def ds(a,b):
    return sum((x-y)**2 for x,y in zip(a,b))

def select(bots, origin, fast):
    nearby=[]
    for i,bot in enumerate(bots):
        if not bot["alive"] or bot["blocked"]:continue
        if fast and bot["registered"] and bot["dormant_hidden"] and (
                ds(bot["cached"],origin)>=48.**2):
            continue
        d=ds(bot["actual"],origin)
        if d>=42.**2:continue
        nearby.append((d,i))
    nearby.sort()
    # Same six-nearest candidate budget and first unobstructed LOS.
    for _,i in nearby[:6]:
        if bots[i]["los"]:return i
    return None

class AlternativeRivalDormantPrefilter(unittest.TestCase):
    def test_native_transform_and_physical_los_stay_authoritative(self):
        for s in ("rivalSpatialRegistered[i] && bot.IsDormantAimInvisible",
                  "(rivalSpatialPositions[i] - origin).sqrMagnitude >=",
                  "48f * 48f", "float sqr = (bot.transform.position - origin).sqrMagnitude",
                  "if (sqr >= maxSqr) continue;", "if (slot >= AlternativeBudget) continue;",
                  "if (used < AlternativeBudget) used++;",
                  "!hunter.HasSightToRival(bot.transform)"):
            self.assertIn(s,SCAN)
        self.assertLess(SCAN.index("rivalSpatialRegistered[i] &&"),
                        SCAN.index("bot.transform == blocked"))
        self.assertLess(SCAN.index("bot.transform == blocked"),
                        SCAN.index("float sqr = (bot.transform.position"))
        self.assertLess(SCAN.index("float sqr = (bot.transform.position"),
                        SCAN.index("hunter.HasSightToRival(bot.transform)"))
        self.assertIn("IsDormantAimInvisible => centrallyDormant && !lastCameraRelevant",SRC)
        self.assertIn("rivalSpatialPositions[slot] = position;",SRC)

    def test_random_100_bot_6_nearest_parity(self):
        rng=random.Random(20261123)
        for _ in range(300):
            origin=(rng.uniform(-800,800),rng.uniform(-10,70),rng.uniform(-800,800))
            bots=[]
            for i in range(100):
                span=57 if i<36 else 600
                actual=tuple(v+rng.uniform(-span,span) for v in origin)
                cached=tuple(v+rng.uniform(-2.5,2.5) for v in actual)
                bots.append(dict(actual=actual,cached=cached,
                     alive=rng.random()>.1,blocked=rng.random()<.03,
                     registered=rng.random()>.07,dormant_hidden=rng.random()>.35,
                     los=rng.random()>.6))
            self.assertEqual(select(bots,origin,False),select(bots,origin,True))

    def test_visible_teleport_and_unregistered_bypass_stale_cache(self):
        origin=(0.,0.,0.)
        for registered,hidden in ((True,False),(False,True)):
            bots=[dict(actual=(3.,0.,0.),cached=(900.,0.,900.),
                       alive=True,blocked=False,registered=registered,
                       dormant_hidden=hidden,los=True)]
            self.assertEqual(select(bots,origin,False),0)
            self.assertEqual(select(bots,origin,True),0)
        bot=dict(actual=(41.999,0.,0.),cached=(47.2,0.,0.),
                 alive=True,blocked=False,registered=True,
                 dormant_hidden=True,los=True)
        self.assertEqual(select([bot],origin,True),0)

if __name__=="__main__":
    unittest.main()
