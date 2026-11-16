#!/usr/bin/env python3
"""Source contracts and deterministic gameplay models. Not a Unity runtime test."""
from pathlib import Path
import itertools
import random
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"

def source(name):
    return (SRC / (name + ".cs")).read_text(encoding="utf-8")

def choose_loot(items, explicit, armed, automatic, active, starter_id=-99):
    reachable = [(distance, item_id) for item_id, distance, clear, unlocked in items
                 if active and distance <= 2.15 and clear and unlocked]
    if explicit:
        return min(reachable, default=(None, None))[1]
    if armed or not automatic:
        return None
    eligible = [item for item in reachable if item[1] >= 0 or item[1] == starter_id]
    return min(eligible, default=(None, None))[1]

def visible(hits, length, receiver):
    return not any(distance < length - .002 and owner not in ("self", receiver)
                   for distance, owner in hits)

class RealPlayerAuthority(unittest.TestCase):
    def test_fire_late_callback(self):
        s = source("AarisBattleActions")
        start = s.index("public void SetFireHeld(bool held)")
        guard = s.index("if (held && (!isActiveAndEnabled || Time.timeScale <= 0f))", start)
        self.assertLess(guard, s.index("fireHeld = held;", start))
        self.assertIn("CancelPendingCombatInput();", s[guard:s.index("fireHeld = held;", start)])

    def test_punch_candidate_visibility_and_overflow(self):
        s = source("AarisBattleActions")
        self.assertLess(s.index("HasPunchLineOfSight(origin, nearest, receiver, mask)"),
                        s.index("distance = candidateDistance;"))
        self.assertIn("Physics.RaycastNonAlloc(ray, punchSightHits, length,", s)
        self.assertIn("hits = Physics.RaycastAll(ray, length, mask, QueryTriggerInteraction.Ignore);", s)
        self.assertIn("c.transform.IsChildOf(transform)", s)
        self.assertIn("FindPunchReceiver(c) == receiver", s)
        for hits in itertools.permutations([(0.1,"self"),(.9,"enemy"),(.5,"wall")]):
            self.assertFalse(visible(hits, 1, "enemy"))
        for hits in itertools.permutations([(0.1,"self"),(.4,"enemy"),(.9,"enemy")]):
            self.assertTrue(visible(hits, 1, "enemy"))
        candidates = [(0.7, [(0.4,"wall")]), (1.0, [(0.5,"self"), (.9,"enemy")])]
        self.assertEqual(min(d for d, h in candidates if visible(h, d, "enemy")), 1.0)

    def test_gloo_invalid_release_cancels_before_queries(self):
        s = source("AarisOfflineGlooWall")
        self.assertIn("Time.timeScale <= 0f || motor == null || !motor.isActiveAndEnabled", s)
        release = s[s.index("public void ReleaseAndDeploy()"):s.index("private int CompleteRayHits")]
        self.assertLess(release.index("if (!CanUse) { CancelAim(); return; }"),
                        release.index("UpdatePlacement();"))

    def test_loot_control_and_candidate_separation(self):
        s = source("AarisWeaponLootWorld")
        self.assertIn("footMotor.isActiveAndEnabled", s)
        self.assertIn("inventory.isActiveAndEnabled", s)
        self.assertIn("drop.HasLanded", s)
        self.assertIn("if (!CanInteract || pickup == null", s)
        self.assertIn("AarisWeaponPickup chosen = explicitPickup ? bestPickup : bestAutoPickup;", s)
        self.assertIn("!IsAutoPickupEligible(pickup) || IsBlockedDrop(pickup)", s)
        items = [(-1,.4,True,True),(25,1.1,True,True),(26,.3,False,True),
                 (27,.1,True,False)]
        rng = random.Random(1611)
        for _ in range(1000):
            rng.shuffle(items)
            self.assertEqual(choose_loot(items,False,False,True,True),25)
            self.assertEqual(choose_loot(items,True,False,True,True),-1)
            self.assertIsNone(choose_loot(items,False,False,True,False))
            self.assertIsNone(choose_loot(items,True,False,True,False))
            self.assertIsNone(choose_loot(items,False,True,True,True))
        self.assertEqual(choose_loot([(-99,.5,True,True)],False,False,True,True),-99)

    def test_landing_nearest_external_contact(self):
        s = source("AarisOfflineDrop")
        self.assertIn("TryFirstExternalLandingHit(probe, 1.60f, out RaycastHit floor)", s)
        helper = s[s.index("private bool TryFirstExternalLandingHit"):s.index("private bool TryNativeTerrainTouchdown")]
        self.assertIn("c.transform.IsChildOf(transform)", helper)
        self.assertIn("hits[i].distance >= nearest", helper)
        self.assertIn("hits = Physics.RaycastAll", helper)
        for hits in itertools.permutations([(.1,"self"),(.6,"roof"),(1.1,"terrain")]):
            self.assertEqual(min(h for h in hits if h[1] != "self"),(.6,"roof"))

    def test_grenade_buffer_reuse(self):
        s = source("AarisOfflineFragGrenade")
        self.assertIn("private readonly List<MonoBehaviour> damageComponents", s)
        self.assertIn("t.GetComponents(damageComponents);", s)
        self.assertNotIn("t.GetComponents<MonoBehaviour>()", s)
        self.assertIn("blastTargets.TryGetValue(target, out var previous)", s)

if __name__ == "__main__":
    unittest.main()
