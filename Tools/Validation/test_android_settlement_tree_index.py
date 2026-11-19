#!/usr/bin/env python3
"""Sparse Android terrain-tree clearance versus legacy brute force.

Static source + deterministic geometry tests, not Unity Play Mode.
"""
import math
import random
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CODE = (ROOT / "Assets/AarisWorld/Scripts/AarisSettlementExpansion.cs").read_text()
CELL = 32.0
MAX_RADIUS = 3.8
RELOCATION = 26.0
OFFSETS = [
    (0, 0), (0, 18), (18, 0), (0, -18), (-18, 0),
    (18, 18), (-18, 18), (18, -18), (-18, -18),
    (0, 26), (26, 0), (0, -26), (-26, 0)
]


def cell(x, z):
    return math.floor(x / CELL), math.floor(z / CELL)


def half_extents(site):
    _, _, width, depth, yaw = site
    a = math.radians(yaw)
    c, s = abs(math.cos(a)), abs(math.sin(a))
    return ((width * c + depth * s) * .5 + 2.0,
            (width * s + depth * c) * .5 + 2.0)


def all_cells(x, z, hx, hz, extra):
    x0, z0 = cell(x - hx - extra, z - hz - extra)
    x1, z1 = cell(x + hx + extra, z + hz + extra)
    return {(cx, cz) for cx in range(x0, x1 + 1)
            for cz in range(z0, z1 + 1)}


def terrain_touch(site, tile):
    x, z, _, _, _ = site
    hx, hz = half_extents(site)
    px, pz, sx, sz = tile
    return not (x + hx + MAX_RADIUS < px or
                x - hx - MAX_RADIUS > px + sx or
                z + hz + MAX_RADIUS < pz or
                z - hz - MAX_RADIUS > pz + sz)


def clear(site, tree):
    x, z, _, _, _ = site
    hx, hz = half_extents(site)
    tx, tz, width_scale = tree
    margin = min(MAX_RADIUS, max(.5, width_scale * 1.5))
    return abs(tx - x) < hx + margin and abs(tz - z) < hz + margin


class SparseTreeClearanceContracts(unittest.TestCase):
    def test_android_batches_native_reads(self):
        self.assertIn("private const int AndroidTreeSamplesPerFrame = 1024;", CODE)
        self.assertIn("TreeInstance tree = data.GetTreeInstance(i);", CODE)
        self.assertIn("if (mobile && (i + 1) % AndroidTreeSamplesPerFrame == 0)", CODE)
        self.assertNotIn("treeCache[t] = t.terrainData.treeInstances;", CODE)
        self.assertIn("relevantTreeCells.Contains(key)", CODE)
        self.assertIn("treeCache.Clear();", CODE)
        self.assertIn("relevantTreeCells.Clear();", CODE)

    def test_simulated_cell_index_matches_exact_brute_force(self):
        rng = random.Random(8675309)
        tiles = [(x, z, 1000, 1000) for x in (-1000, 0) for z in (-1000, 0)]
        sites = []
        for i in range(21):
            sites.append((rng.uniform(-940, 940), rng.uniform(-940, 940),
                          rng.uniform(4, 36), rng.uniform(4, 30),
                          rng.choice([0, 33, 45, 90, 135, 176])))
        sites.append((-1.0, -1.0, 18.0, 25.0, 45.0))
        sites.append((1.0, 1.0, 20.0, 12.0, 90.0))
        trees = {}
        for tile in tiles:
            px, pz, sx, sz = tile
            trees[tile] = [(rng.uniform(px, px + sx), rng.uniform(pz, pz + sz),
                            rng.uniform(.2, 3.8)) for _ in range(400)]
        relevant = set()
        for site in sites:
            x, z, *_ = site
            hx, hz = half_extents(site)
            relevant |= all_cells(x, z, hx, hz, MAX_RADIUS + RELOCATION)
        sparse = {}
        for tile, original in trees.items():
            bins = {}
            for tree in original:
                key = cell(tree[0], tree[1])
                if key in relevant:
                    bins.setdefault(key, []).append(tree)
            sparse[tile] = bins

        for site in sites:
            for dx, dz in OFFSETS:
                displaced = (site[0] + dx, site[1] + dz, *site[2:])
                hx, hz = half_extents(displaced)
                expected = any(
                    terrain_touch(displaced, tile) and
                    any(clear(displaced, tree) for tree in trees[tile])
                    for tile in tiles)
                nearby = all_cells(displaced[0], displaced[1], hx, hz, MAX_RADIUS)
                actual = any(
                    terrain_touch(displaced, tile) and
                    any(clear(displaced, tree)
                        for key in nearby for tree in sparse[tile].get(key, ()))
                    for tile in tiles)
                self.assertEqual(expected, actual, (site, dx, dz))

    def test_tree_margin_boundary(self):
        site = (0.0, 0.0, 20.0, 20.0, 0.0)
        hx, hz = half_extents(site)
        self.assertTrue(clear(site, (hx + 3.79, 0.0, 3.0)))
        self.assertFalse(clear(site, (hx + 3.81, 0.0, 3.0)))
        self.assertIn(cell(hx + 3.79, 0.0),
                      all_cells(0, 0, hx, hz, MAX_RADIUS))


if __name__ == "__main__":
    unittest.main(verbosity=2)
