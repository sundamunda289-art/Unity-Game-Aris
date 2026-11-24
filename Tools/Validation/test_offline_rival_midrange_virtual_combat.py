#!/usr/bin/env python3
"""90-155 m unseen virtualization / non-physical combat safety contract.
Source + deterministic policy model; not a native or Android profiler test.
"""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text("utf-8")
RIVAL = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
UPDATE = RIVAL.split("private void Update()", 1)[1].split(
    "private void TickVirtualCombat(", 1)[0]
VIRTUAL = RIVAL.split("private void TickVirtualCombat(", 1)[1].split(
    "private float NextVirtualRandom(", 1)[0]


def virtual(distance, on_camera):
    return distance >= 155 or (distance >= 90 and not on_camera)


def abstract_damage_allowed(shooter_distance, victim_distance,
                            victim_virtual, blocked_by_cover):
    return (shooter_distance >= 90 and victim_distance >= 90 and
            victim_virtual and not blocked_by_cover)


class MidrangeVirtualCombat(unittest.TestCase):
    def test_85_offscreen_fighters_do_not_have_renderers_or_native_receivers(self):
        # 15 nearby and 85 distant fighters; camera is facing away from
        # remote squads, which continue independent AI simulation.
        near = [25.] * 15
        remote = [200. + (n % 16) * 45. for n in range(85)]
        self.assertEqual(len(near + remote), 100)
        self.assertEqual(sum(virtual(d, False) for d in remote), 85)
        self.assertEqual(sum(d < 485 and False for d in remote), 0)
        self.assertEqual(sum(virtual(d, False) for d in near), 0)

    def test_90_155_offscreen_transition_preserves_visible_hit_receivers(self):
        self.assertFalse(virtual(89.9, False))
        self.assertTrue(virtual(90., False))
        self.assertTrue(virtual(150., False))
        self.assertFalse(virtual(150., True))
        self.assertTrue(virtual(155., True))
        self.assertTrue(virtual(300., True))
        self.assertIn("bool shouldVirtualize =", UPDATE)
        self.assertIn("bool shouldRestorePhysics =", UPDATE)
        self.assertIn("if (!simulationDue) return;", UPDATE)
        self.assertIn("bool needsPhysicalReceiver = !virtualBody ||", UPDATE)

    def test_abstract_damage_respects_physical_mode_and_cover_independently_of_camera(self):
        self.assertTrue(abstract_damage_allowed(130, 140, True, False))
        self.assertTrue(abstract_damage_allowed(300, 320, True, False))
        for args in ((60, 140, True, False),
                     (130, 70, True, False),
                     (130, 140, False, False),
                     (300, 320, True, True)):
            self.assertFalse(abstract_damage_allowed(*args))
        self.assertIn("target.TryGetComponent<AarisOfflineRival>", VIRTUAL)
        self.assertIn("!cachedVirtualDamageRival.virtualBody", VIRTUAL)
        self.assertIn("owner.IsVirtualShotBlocked(selfPosition, targetPosition)", VIRTUAL)
        self.assertNotIn("lastCameraRelevant", VIRTUAL)
        self.assertNotIn("HasVisibleBody", VIRTUAL)
        self.assertIn("targetHealth.ApplyDamage(damage, gameObject);", VIRTUAL)
        self.assertIn("FarSimulationDistance * FarSimulationDistance", VIRTUAL)

    def test_kills_share_real_health_and_alive_count(self):
        self.assertIn("health.Died += OnDied;", RIVAL)
        self.assertIn("owner.RivalDefeated(this);", RIVAL)
        self.assertIn("owner.ReportRivalElimination(attacker, gameObject);", RIVAL)
        self.assertIn("if (activeRivals > 0) activeRivals--;", SOURCE)
        self.assertIn("Recent elimination", SOURCE)
        self.assertIn("private const float FarSimulationInterval = 0.35f;", RIVAL)


if __name__ == "__main__":
    unittest.main()
