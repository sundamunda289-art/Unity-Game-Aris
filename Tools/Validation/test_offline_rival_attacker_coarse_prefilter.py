#!/usr/bin/env python3
"""42m player-attacker scan: conservative 48m spatial coarse reject.

Contract + deterministic 100-bot parity. Not a Unity/Android FPS benchmark.
"""
from pathlib import Path
import random
import unittest

SRC = (Path(__file__).resolve().parents[2] /
       "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
SCAN = SRC.split("private void RefreshPlayerAttackers()", 1)[1].split(
    "internal bool MayAttackPlayer(", 1)[0]
TRACK = SRC.split("internal void TrackRivalPosition(", 1)[1].split(
    "private void BootstrapRivalSpatialIndex()", 1)[0]


def distance_sqr(a, b):
    return sum((x - y) ** 2 for x, y in zip(a, b))


def select_two(bots, player, fast=False):
    candidates = []
    for i, bot in enumerate(bots):
        if not bot["alive"]:
            continue
        if fast and bot["registered"] and distance_sqr(bot["cached"], player) >= 48 ** 2:
            continue
        if not bot["visible"]:
            continue
        actual = distance_sqr(bot["position"], player)
        if actual >= 42 ** 2 or not bot["physical_los"]:
            continue
        # The C# selection has strict < ties and traverses roster slot order.
        candidates.append((actual, i))
    return [index for _, index in sorted(candidates)[:2]]


class SpatialAttackerCoarseReject(unittest.TestCase):
    def test_every_distant_bot_is_rejected_before_native_transform_or_los(self):
        self.assertIn("rivalSpatialRegistered[i] &&", SCAN)
        self.assertIn("rivalSpatialPositions[i] - playerPosition", SCAN)
        self.assertIn("48f * 48f) continue;", SCAN)
        self.assertIn("if (!bot.HasVisibleBody) continue;", SCAN)
        self.assertIn(
            "float distance = (bot.transform.position - playerPosition).sqrMagnitude;",
            SCAN)
        self.assertIn("distance >= second || !bot.HasPlayerSightForSelection()", SCAN)
        prefilter = SCAN.index("rivalSpatialRegistered[i] &&")
        self.assertLess(prefilter, SCAN.index("if (!bot.HasVisibleBody)"))
        self.assertLess(prefilter, SCAN.index("float distance = (bot.transform.position"))
        self.assertLess(SCAN.index("if (!bot.HasVisibleBody)"),
                        SCAN.index("float distance = (bot.transform.position"))
        self.assertLess(SCAN.index("float distance = (bot.transform.position"),
                        SCAN.index("bot.HasPlayerSightForSelection()"))
        self.assertIn("int end = Mathf.Min(attackerScanCursor + AttackerSelectionPerFrame,", SCAN)
        self.assertIn("rivals.Length);", SCAN)
        self.assertIn("if (attackerScanCursor != rivals.Length) return;", SCAN)
        self.assertIn("attackerScanFrame == Time.frameCount", SCAN)
        self.assertIn("attackerOne = scanAttackerOne != null && scanAttackerOne.IsAlive", SCAN)
        self.assertIn("attackerTwo = scanAttackerTwo != null && scanAttackerTwo.IsAlive", SCAN)

    def test_world_cache_is_fresh_on_movement_even_inside_a_cell(self):
        self.assertLess(TRACK.index("rivalSpatialPositions[slot] = position;"),
                        TRACK.index("if (rivalSpatialRegistered[slot] &&"))
        self.assertIn("owner.TrackRivalPosition(spatialSlot, this, afterMove);", SRC)
        self.assertIn("owner.TrackRivalPosition(spatialSlot, this, next);", SRC)
        self.assertIn("TrackRivalPosition(index, rival, point);", SRC)
        self.assertIn("UntrackRivalPosition(rival.SpatialSlot, rival);", SRC)

    def test_100_bots_match_two_slot_physical_los_and_roster_ties(self):
        rng = random.Random(20261123)
        for case in range(220):
            player = (rng.uniform(-900, 900), rng.uniform(-30, 75),
                      rng.uniform(-900, 900))
            bots = []
            for i in range(100):
                # Include dense threat bands, exact 42m boundary, distant
                # remote squads, same-slot ties and negative grid coordinates.
                if i < 30:
                    dx = rng.uniform(-50, 50)
                    dz = rng.uniform(-50, 50)
                    pos = (player[0] + dx, player[1] + rng.uniform(-2, 2),
                           player[2] + dz)
                elif i == 30:
                    pos = (player[0] + 42.0, player[1], player[2])
                else:
                    pos = (player[0] + rng.choice((-1, 1)) * rng.uniform(55, 1200),
                           player[1] + rng.uniform(-8, 8),
                           player[2] + rng.choice((-1, 1)) * rng.uniform(55, 1200))
                # At most 3m cache error covers ordinary movement between
                # position-tracking ticks and the 42-to-48m safety margin.
                cached = (pos[0] + rng.uniform(-1.6, 1.6),
                          pos[1] + rng.uniform(-1.6, 1.6),
                          pos[2] + rng.uniform(-1.6, 1.6))
                bots.append(dict(alive=rng.random() > .13,
                                 registered=rng.random() > .08,
                                 position=pos, cached=cached,
                                 visible=rng.random() > .34,
                                 physical_los=rng.random() > .55))
            self.assertEqual(select_two(bots, player, fast=True),
                             select_two(bots, player), f"scenario {case}")

    def test_boundary_and_unregistered_entry_still_use_live_geometry(self):
        player = (0., 0., 0.)
        bots = [
            dict(alive=True, registered=True, position=(41.99, 0., 0.),
                 cached=(47.0, 0., 0.), visible=True, physical_los=True),
            dict(alive=True, registered=True, position=(42., 0., 0.),
                 cached=(40., 0., 0.), visible=True, physical_los=True),
            dict(alive=True, registered=False, position=(2., 0., 0.),
                 cached=(900., 0., 0.), visible=True, physical_los=True),
            dict(alive=True, registered=True, position=(10., 0., 0.),
                 cached=(10., 0., 0.), visible=False, physical_los=True),
        ]
        self.assertEqual(select_two(bots, player, fast=True), [2, 0])
        self.assertEqual(select_two(bots, player, fast=False), [2, 0])

    def test_profiler_marker_only_in_editor_or_development_player(self):
        self.assertEqual(SRC.count(
            'new ProfilerMarker("Aaris.AI.PlayerAttackerSelection")'), 1)
        self.assertIn("private static readonly ProfilerMarker k_PlayerThreat =", SRC)
        self.assertIn("using var aiProfilerScope = k_PlayerThreat.Auto();", SCAN)
        self.assertIn(
            "private void RefreshPlayerAttackers()\n"
            "        {\n"
            "#if UNITY_EDITOR || DEVELOPMENT_BUILD\n"
            "            using var aiProfilerScope = k_PlayerThreat.Auto();\n"
            "#endif", SRC)


if __name__ == "__main__":
    unittest.main()
