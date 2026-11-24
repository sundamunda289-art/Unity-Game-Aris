#!/usr/bin/env python3
"""Guarantee distant bots avoid per-instance physical query buffers."""
from pathlib import Path
import unittest

SRC=(Path(__file__).resolve().parents[2]/'Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs').read_text()
RIVAL=SRC.split('internal sealed class AarisOfflineRival',1)[1]

class DistantCombatMemory(unittest.TestCase):
 def test_distant_spawn_does_not_allocate_query_arrays(self):
  for label in ('sightHits','crowdedSightHits','coverCandidates','crowdedCoverCandidates'):
   self.assertIn('[] '+label+';',RIVAL)
  init=RIVAL.split('internal void Initialize(',1)[1].split('private void EnsurePhysicalCombatBuffers()',1)[0]
  self.assertLess(init.index('virtualBody = true;'),init.index('EnsurePhysicalCombatBuffers();'))
  self.assertIn('else',init)
 def test_all_buffers_prepared_on_local_entry(self):
  helper=RIVAL.split('private void EnsurePhysicalCombatBuffers()',1)[1].split('private void BuildVisual()',1)[0]
  self.assertIn('if (sightHits != null) return;',helper)
  for name,size,kind in [('sightHits',20,'RaycastHit'),('crowdedSightHits',128,'RaycastHit'),('coverCandidates',24,'Collider'),('crowdedCoverCandidates',128,'Collider')]:
   self.assertIn(name+' = new '+kind+'['+str(size)+'];',helper)
  update=RIVAL.split('private void Update()',1)[1].split('private void TickVirtualCombat(',1)[0]
  self.assertLess(update.index('TickVirtualCombat(now, dt, playerPosition);'),update.index('EnsurePhysicalCombatBuffers();'))
  self.assertLess(update.index('EnsurePhysicalCombatBuffers();'),update.index('if (now >= nextThink)'))
 def test_los_and_hit_callback_guards(self):
  for signature in ('private bool TryGetSightHit(', 'private bool MuzzlePathClear(', 'private void OnDamaged('):
   segment=RIVAL.split(signature,1)[1].split('private ',1)[0]
   self.assertIn('EnsurePhysicalCombatBuffers();',segment)
  self.assertIn('if (virtualBody || Time.time < nextCoverQuery) return;',RIVAL)

if __name__=='__main__':unittest.main()
