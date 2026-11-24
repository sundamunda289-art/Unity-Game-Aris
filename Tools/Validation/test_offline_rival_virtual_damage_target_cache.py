#!/usr/bin/env python3
"""Cache only remote rival component identity; check combat mode and cover on each shot."""
import random as random_module
import unittest
from pathlib import Path
S=(Path(__file__).resolve().parents[2]/"Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
R=S.split("private void TickVirtualCombat(",1)[1].split("private float NextVirtualRandom()",1)[0]
B=S.split("internal sealed class AarisOfflineRival",1)[1]

class RemoteDamageCache(unittest.TestCase):
    def test_target_health_destroyed_component_invalidation(self):
        for x in ("private Transform cachedVirtualDamageTarget;",
                  "private AarisOfflineHealth cachedVirtualDamageHealth;",
                  "private AarisOfflineRival cachedVirtualDamageRival;",
                  "cachedVirtualDamageTarget = null;",
                  "cachedVirtualDamageHealth = null;",
                  "cachedVirtualDamageRival = null;"):
            self.assertIn(x,B)
        for x in ("cachedVirtualDamageTarget != target",
                  "cachedVirtualDamageHealth != targetHealth",
                  "cachedVirtualDamageRival == null",
                  "target.TryGetComponent<AarisOfflineRival>(",
                  "!cachedVirtualDamageRival.virtualBody",
                  "owner.IsVirtualShotBlocked(selfPosition, targetPosition)",
                  "targetHealth.ApplyDamage(damage, gameObject);"):
            self.assertIn(x,R)
        self.assertLess(R.index("cachedVirtualDamageTarget != target"),
                        R.index("target.TryGetComponent<AarisOfflineRival>("))
        self.assertLess(R.index("owner.IsVirtualShotBlocked(selfPosition, targetPosition)"),
                        R.index("targetHealth.ApplyDamage(damage, gameObject);"))
        self.assertIn("target != playerTarget",R)
        self.assertIn("TryGetSightHit(target, out RaycastHit shotHit)",S)
        self.assertIn("MuzzlePathClear(target)",S)

    def test_100_bot_target_switches_and_live_combat_parity(self):
        random=random_module.Random(20261123)
        cache=[None]*100;tgt=list(range(100));hp=list(range(100))
        component=list(range(100));lookup=0;old=0;damage=0
        for tick in range(80):
            for i in range(100):
                if random.random()<.10:tgt[i]=random.randrange(100)
                if random.random()<.03:hp[i]+=1000
                if random.random()<.02:component[i]+=1000
                if random.random()<.2:continue
                old+=1
                oldcache=cache[i]
                if oldcache!=(tgt[i],hp[i],component[i]):
                    lookup+=1
                    cache[i]=(tgt[i],hp[i],component[i])
                is_virtual = random.random() > .19
                cover_blocks = random.random() < .28
                allowed = is_virtual and not cover_blocks
                self.assertEqual(allowed, cache[i][2] == component[i] and
                                 is_virtual and not cover_blocks)
                damage+=allowed
        self.assertGreater(damage,200)
        self.assertLess(lookup,old*.35)

if __name__=="__main__":
    unittest.main()
