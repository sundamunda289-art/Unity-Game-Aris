#!/usr/bin/env python3
"""Deterministic geometry parity for retained terrain-tree samples.

Static/algorithmic gate, not Unity compilation or Android benchmarking.
"""
from pathlib import Path
import math
import random
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisSettlementExpansion.cs").read_text()
CELL = 32.0
MARGIN = 3.8
MOVE = 26.0
OFFSETS = ((0, 0), (0, 18), (18, 0), (0, -18), (-18, 0),
           (18, 18), (-18, 18), (18, -18), (-18, -18),
           (0, 26), (26, 0), (0, -26), (-26, 0))


def footprint(site):
    x, z, width, depth, yaw, _ = site
    r = math.radians(yaw)
    co, si = abs(math.cos(r)), abs(math.sin(r))
    return (.5 * (width * co + depth * si) + 2,
            .5 * (width * si + depth * co) + 2)


def cell(x, z):
    return math.floor(x / CELL), math.floor(z / CELL)


def cell_range(site):
    hx, hz = footprint(site)
    x, z, _, _, _, interactive = site
    relocation = MOVE if interactive else 0.0
    marginx = hx + MARGIN + relocation
    marginz = hz + MARGIN + relocation
    x0, z0 = cell(x - marginx, z - marginz)
    x1, z1 = cell(x + marginx, z + marginz)
    return {(cx, cz) for cx in range(x0, x1 + 1)
            for cz in range(z0, z1 + 1)}


def terrain_bounds_might_intersect(site, tile):
    hx, hz = footprint(site)
    x, z, *_ = site
    ox, oz, width, depth = tile
    return not (x + hx + MARGIN < ox or
                x - hx - MARGIN > ox + width or
                z + hz + MARGIN < oz or
                z - hz - MARGIN > oz + depth)


def blocked(site, terrain_tree_sets):
    x, z, *_ = site
    hx, hz = footprint(site)
    for tile, trees in terrain_tree_sets:
        if not terrain_bounds_might_intersect(site, tile):
            continue
        for tx, tz, width_scale in trees:
            margin = min(MARGIN, max(.5, width_scale * 1.5))
            if abs(tx - x) < hx + margin and abs(tz - z) < hz + margin:
                return True
    return False


class SelectiveTreeRetention(unittest.TestCase):
    def test_source_keeps_exact_native_tree_budget_and_guards(self):
        self.assertIn("BuildTreeInterestCells(plan);", SOURCE)
        self.assertIn("HashSet<long> treeInterestCells", SOURCE)
        self.assertIn("TreeInterestCellMetres = 32f", SOURCE)
        self.assertIn("MaximumInteractiveRelocationAxis = 26f", SOURCE)
        self.assertIn("MaximumTreeClearanceMargin = 3.8f", SOURCE)
        self.assertIn("site.interactive", SOURCE)
        self.assertIn("samples.Add(new TreeClearanceSample(x, z, margin));", SOURCE)
        self.assertIn("data.GetTreeInstance(i)", SOURCE)
        self.assertIn("if (mobile && ++processed >= AndroidTreeProbeBudgetPerFrame)", SOURCE)
        self.assertIn("treeInterestCells.Contains(", SOURCE)
        self.assertIn("treeInterestCells.Clear();", SOURCE)
        self.assertIn("treeCache.Clear();", SOURCE)
        self.assertIn("FootprintHalfExtents(site, VegetationMargin", SOURCE)
        self.assertNotIn("data.treeInstances", SOURCE)
        self.assertIn("!float.IsNaN(x)", SOURCE)
        self.assertIn("!float.IsInfinity(z)", SOURCE)

    def test_exact_brute_force_predicate_matches_filtered_forest(self):
        rng = random.Random(20261119)
        tiles = [(ox, oz, 1000.0, 1000.0)
                 for ox in (-1000.0, 0.0) for oz in (-1000.0, 0.0)]
        sites = []
        for i in range(21):
            sites.append((rng.uniform(-850, 850), rng.uniform(-850, 850),
                          rng.uniform(8, 50), rng.uniform(8, 50),
                          rng.choice((0, 15, 45, 90, 117, 178)),
                          i % 4 == 0))
        sites += [(-0.1, -0.1, 28, 23, 45, True),
                  (0.1, 0.1, 17, 20, 90, False),
                  (0.0, -25.0, 32, 26, 135, True)]
        forest = []
        for tile in tiles:
            x, z, sx, sz = tile
            trees = [(rng.uniform(x, x + sx), rng.uniform(z, z + sz),
                      rng.uniform(.2, 4)) for _ in range(1800)]
            trees += [(0, 0, 2.0), (-1.0, -23.0, 2.6)]
            forest.append((tile, trees))

        interested = set().union(*(cell_range(site) for site in sites))
        retained = [(tile, [tree for tree in trees
                            if cell(tree[0], tree[1]) in interested])
                    for tile, trees in forest]
        original_count = sum(len(trees) for _, trees in forest)
        cached_count = sum(len(trees) for _, trees in retained)
        self.assertLess(cached_count, original_count)
        checks = 0
        for site in sites:
            offsets = OFFSETS if site[-1] else [(0, 0)]
            for dx, dz in offsets:
                moved = (site[0] + dx, site[1] + dz, *site[2:])
                self.assertEqual(blocked(moved, forest), blocked(moved, retained),
                                 (site, dx, dz))
                checks += 1
        self.assertGreater(checks, 100)

    def test_cell_boundary_interest_includes_maximum_tree_margin(self):
        site = (-32.01, 31.99, 20, 30, 45, True)
        hx, hz = footprint(site)
        candidates = [
            (site[0] + hx + MARGIN - .01, site[1], 10),
            (site[0] - hx - MARGIN + .01, site[1], 10),
            (site[0] + MOVE, site[1] + hz + MARGIN - .01, 10),
            (site[0], site[1] - MOVE - hz - MARGIN + .01, 10),
        ]
        for x, z, _ in candidates:
            self.assertIn(cell(x, z), cell_range(site))


if __name__ == "__main__":
    unittest.main(verbosity=2)
