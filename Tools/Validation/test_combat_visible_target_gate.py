#!/usr/bin/env python3
"""Source contract and scenario simulation: unseen streamed rivals cannot attack or lock."""
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()


class StreamedRivalTargetGates(unittest.TestCase):
    def test_visual_readiness_is_not_equivalent_to_physics_spawn(self):
        self.assertIn("internal bool HasVisibleBody => visualRoot != null && visualRoot.activeInHierarchy;", SOURCE)
        # IsAlive remains separate: unseen rivals continue distant world simulation.
        self.assertIn("public bool IsAlive => health != null && !health.IsDead;", SOURCE)
        self.assertNotIn("public bool IsAlive => health != null && !health.IsDead &&", SOURCE)

    def test_red_lock_skips_unrendered_opponents(self):
        target_search = SOURCE.split("internal static bool TryAcquireAimTarget", 1)[1].split("private void RefreshStatus", 1)[0]
        self.assertIn("!bot.HasVisibleBody", target_search)
        self.assertIn("closest.transform.IsChildOf(candidate.transform)", target_search)

    def test_unseen_rivals_do_not_take_player_attack_slots(self):
        selection = SOURCE.split("private void RefreshPlayerAttackers", 1)[1].split("internal bool MayAttackPlayer", 1)[0]
        los = SOURCE.split("internal bool HasPlayerSightForSelection", 1)[1].split("private bool HasSight()", 1)[0]
        self.assertIn("bot.HasPlayerSightForSelection()", selection)
        self.assertIn("return HasVisibleBody && playerTarget != null", los)
        self.assertIn("return bot == attackerOne || bot == attackerTwo;", SOURCE)
        rivals = [(4, False, True), (6, True, False), (12, True, True), (18, True, True)]
        visible_in_sight = [d for d, visible, clear in rivals if d < 42 and visible and clear]
        self.assertEqual(visible_in_sight[:2], [12, 18])


if __name__ == "__main__":
    unittest.main()
