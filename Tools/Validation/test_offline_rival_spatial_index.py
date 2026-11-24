#!/usr/bin/env python3
"""110m bot spatial index: signed-grid, collision and nearest-target parity.

This is a source contract + deterministic model, not a Unity Physics test.
"""
from pathlib import Path
import random
import math
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
DIRECTOR, RIVAL = SRC.split("internal sealed class AarisOfflineRival", 1)
BENCH = (ROOT / "Assets/AarisWorld/Editor/AarisBotFrameBenchmark.cs").read_text()


def cell(value):
    return math.floor(value / 110.0)


def signed_hash(x, z):
    # Simulates the two unchecked signed 32-bit products in RivalBucket.
    n = ((x * 73856093) ^ (z * 19349663)) & 0xffffffff
    return n & 255


class ModelIndex:
    def __init__(self):
        self.head = [-1] * 256
        self.nxt = [-1] * 100
        self.prev = [-1] * 100
        self.key = [None] * 100
        self.bucket = [0] * 100
        self.alive = [False] * 100
        self.pos = [(0., 0., 0.)] * 100

    def remove(self, slot):
        if self.key[slot] is None:
            return
        before, after = self.prev[slot], self.nxt[slot]
        if before == -1:
            self.head[self.bucket[slot]] = after
        else:
            self.nxt[before] = after
        if after >= 0:
            self.prev[after] = before
        self.key[slot] = None
        self.alive[slot] = False

    def set(self, slot, pos):
        self.pos[slot] = pos
        x, z = cell(pos[0]), cell(pos[2])
        target_key = (x, z)
        if self.key[slot] == target_key:
            return
        self.remove(slot)
        bucket = signed_hash(x, z)
        before = self.head[bucket]
        self.prev[slot] = -1
        self.nxt[slot] = before
        if before >= 0:
            self.prev[before] = slot
        self.head[bucket] = slot
        self.key[slot] = target_key
        self.bucket[slot] = bucket
        self.alive[slot] = True

    def query(self, subject):
        origin = self.pos[subject]
        cx, cz = cell(origin[0]), cell(origin[2])
        best, best_index = 110.0 ** 2, 100
        for x in range(cx - 1, cx + 2):
            for z in range(cz - 1, cz + 2):
                i = self.head[signed_hash(x, z)]
                while i >= 0:
                    if i != subject and self.alive[i] and self.key[i] == (x, z):
                        p = self.pos[i]
                        d = sum((a - b) ** 2 for a, b in zip(origin, p))
                        if d < 110.0 ** 2 and (d < best or
                                d == best and i < best_index):
                            best, best_index = d, i
                    i = self.nxt[i]
        return best_index if best_index < 100 else None

    def brute(self, subject):
        origin = self.pos[subject]
        best = 110.0 ** 2
        index = None
        for i, p in enumerate(self.pos):
            if i == subject or not self.alive[i]:
                continue
            d = sum((a - b) ** 2 for a, b in zip(origin, p))
            if d < best:
                best, index = d, i
        return index


class SpatialRivalIndexContract(unittest.TestCase):
    def test_one_time_shared_arrays_and_no_all_roster_scan_in_query(self):
        self.assertIn("private const int RivalSpatialBuckets = 256;", DIRECTOR)
        self.assertIn("private readonly int[] rivalBucketHeads =", DIRECTOR)
        self.assertIn("private readonly long[] rivalSpatialKeys =", DIRECTOR)
        self.assertIn("private void BootstrapRivalSpatialIndex()", DIRECTOR)
        hunt = DIRECTOR.split("internal bool FindRivalOpponent(", 1)[1].split(
            "internal bool TryFindVisibleRivalAlternative", 1)[0]
        self.assertIn("for (int x = cx - 1; x <= cx + 1; x++)", hunt)
        self.assertIn("for (int z = cz - 1; z <= cz + 1; z++)", hunt)
        self.assertIn("rivalSpatialKeys[i] != key", hunt)
        self.assertIn("rivalSpatialPositions[i] - origin", hunt)
        self.assertNotIn("bot.transform.position - origin", hunt)
        self.assertIn("private readonly Vector3[] rivalSpatialPositions =", DIRECTOR)
        self.assertNotIn("for (int i = 0; i < rivals.Length; i++)", hunt)
        self.assertNotIn("new List", hunt)

    def test_hunt_resolves_native_transform_only_for_final_winner(self):
        hunt = DIRECTOR.split("internal bool FindRivalOpponent(", 1)[1].split(
            "internal bool TryFindVisibleRivalAlternative", 1)[0]
        self.assertLess(hunt.index("if (d >= RivalHuntRadius"),
                        hunt.index("AarisOfflineRival bot = rivals[i]"))
        self.assertNotIn("victim = bot.transform", hunt)
        self.assertEqual(hunt.count("victim = winner.transform;"), 1)
        self.assertLess(hunt.index("if (nearestIndex == RivalCount) return false;"),
                        hunt.index("victim = winner.transform;"))

    def test_spawn_movement_death_and_benchmark_hooked_up(self):
        self.assertIn("TrackRivalPosition(index, rival, point);", DIRECTOR)
        self.assertIn("UntrackRivalPosition(rival.SpatialSlot, rival);", DIRECTOR)
        self.assertIn("private int spatialSlot = -1;", RIVAL)
        self.assertIn("spatialSlot = encounterSlot;", RIVAL)
        self.assertIn("owner.TrackRivalPosition(spatialSlot, this, afterMove);", RIVAL)
        self.assertIn("owner.TrackRivalPosition(spatialSlot, this, next);", RIVAL)
        self.assertIn("owner.UntrackRivalPosition(spatialSlot, this);", RIVAL)
        self.assertIn('Set(brain, "spatialSlot", i);', BENCH)
        self.assertIn("private const float RivalHuntRadius = 110f;", DIRECTOR)

    def test_every_move_refreshes_position_cache_even_inside_same_cell(self):
        track = DIRECTOR.split("internal void TrackRivalPosition(", 1)[1].split(
            "private void BootstrapRivalSpatialIndex", 1)[0]
        self.assertLess(track.index("rivalSpatialPositions[slot] = position;"),
                        track.index("if (rivalSpatialRegistered[slot] &&"))
        self.assertIn("TrackRivalPosition(index, rival, point);", DIRECTOR)
        self.assertIn("owner.TrackRivalPosition(spatialSlot, this, next);", RIVAL)
        self.assertIn("owner.TrackRivalPosition(spatialSlot, this, afterMove);", RIVAL)
        grid = ModelIndex()
        grid.set(0, (0., 0., 0.))
        grid.set(1, (50., 0., 0.))
        grid.set(2, (90., 0., 0.))
        self.assertEqual(grid.query(0), 1)
        # Moving in the exact same 110m cell must change opponent priority.
        grid.set(2, (9., 0., 0.))
        self.assertEqual(grid.query(0), 2)
        grid.set(2, (90., 0., 0.))
        self.assertEqual(grid.query(0), 1)
        # Y still contributes to true 3D range; no 2D false targets.
        grid.set(1, (10., 140., 0.))
        self.assertIsNone(grid.query(1))
        self.assertEqual(grid.query(0), 2)

    def test_dev_only_profiler_labels_are_stripped_from_release_players(self):
        labels = ("Aaris.AI.DormantScheduler",
                  "Aaris.AI.SpatialOpponentLookup",
                  "Aaris.AI.RemoteCombatTick")
        for label in labels:
            self.assertEqual(SRC.count('new ProfilerMarker("' + label + '")'), 1)
        self.assertIn("using Unity.Profiling;", SRC)
        self.assertIn("using var aiProfilerScope = k_DormantScan.Auto();", DIRECTOR)
        self.assertIn("using var aiProfilerScope = k_SpatialHunt.Auto();", DIRECTOR)
        self.assertIn("using var aiProfilerScope = k_RemoteCombat.Auto();", RIVAL)
        self.assertIn("private static readonly ProfilerMarker k_RemoteCombat =", RIVAL)
        self.assertNotIn("private static readonly ProfilerMarker k_RemoteCombat =", DIRECTOR)
        for field in ("k_DormantScan", "k_SpatialHunt", "k_RemoteCombat"):
            statement = f"using var aiProfilerScope = {field}.Auto();"
            offset = SRC.index(statement)
            self.assertIn("#if UNITY_EDITOR || DEVELOPMENT_BUILD",
                          SRC[offset - 90:offset])
            self.assertTrue(SRC[offset + len(statement):].lstrip().startswith("#endif"))

    def test_negative_coordinates_boundaries_and_ties(self):
        g = ModelIndex()
        for i, point in enumerate([
            (-110.0, 0., -110.0), (-109.9, 0., -110.0),
            (-220.0, 0., -110.), (-110., 0., -220.),
            (-109., 0., -109.), (0., 0., 0.),
        ]):
            g.set(i, point)
        for i in range(6):
            self.assertEqual(g.query(i), g.brute(i))
        # Slot ordering must win when distances are exactly equal.
        g = ModelIndex()
        g.set(0, (0., 0., 0.))
        g.set(1, (10., 0., 0.))
        g.set(2, (-10., 0., 0.))
        self.assertEqual(g.query(0), 1)
        g.remove(1)
        self.assertEqual(g.query(0), 2)
        # 110 metres is an exclusive search radius.
        g.set(2, (-110., 0., 0.))
        self.assertIsNone(g.query(0))

    def test_randomized_100_bot_queries_after_movement_and_death(self):
        rng = random.Random(20261122)
        for seed in range(4):
            g = ModelIndex()
            for i in range(100):
                g.set(i, (rng.uniform(-1000, 1000), rng.uniform(-35, 60),
                          rng.uniform(-1000, 1000)))
            for n in range(120):
                slot = rng.randrange(100)
                if rng.random() < .13:
                    g.remove(slot)
                else:
                    x, y, z = g.pos[slot]
                    # Also model occasional long-distance bot teleports.
                    delta = 250. if rng.random() < .03 else 8.
                    g.set(slot, (x + rng.uniform(-delta, delta), y,
                                 z + rng.uniform(-delta, delta)))
                for subject in range(100):
                    if g.alive[subject]:
                        self.assertEqual(g.query(subject), g.brute(subject),
                                         (seed, n, subject))

    def test_collision_buckets_do_not_leak_other_spatial_cells(self):
        g = ModelIndex()
        first = (0, 0)
        collision = next((x, z) for x in range(1, 200) for z in range(1, 200)
                         if signed_hash(x, z) == signed_hash(*first))
        g.set(0, (1., 0., 1.))
        g.set(1, (collision[0] * 110. + 1., 0.,
                  collision[1] * 110. + 1.))
        self.assertIsNone(g.query(0))
        self.assertIsNone(g.query(1))
        for n in range(10):
            g.remove(1)
            g.set(1, (collision[0] * 110. + 2. + n, 0.,
                      collision[1] * 110. + 2.))
            self.assertIsNone(g.query(0))


if __name__ == "__main__":
    unittest.main()
