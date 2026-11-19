#!/usr/bin/env python3
"""Source and predicate parity checks for 4GB Android tree-clearance pacing.

This is intentionally a license-free static/algorithmic test: it does not
claim Unity compilation, scene rendering, or on-phone frame-time evidence.
"""
import math
import random
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[2] / "Assets" / "AarisWorld" /
          "Scripts" / "AarisSettlementExpansion.cs").read_text(encoding="utf-8")
INDEX = (Path(__file__).resolve().parents[2] / "Assets" / "AarisWorld" /
         "Scripts" / "AarisTreeClearanceIndex.cs").read_text(encoding="utf-8")


def half_extents(site):
    x, z, width, depth, yaw = site
    angle = math.radians(yaw)
    cos, sin = abs(math.cos(angle)), abs(math.sin(angle))
    return ((width*cos + depth*sin) * .5 + 2.0,
            (width*sin + depth*cos) * .5 + 2.0)


def hits_native(site, tiles):
    half_x, half_z = half_extents(site)
    for origin_x, origin_z, size_x, size_z, trees in tiles:
        if (site[0] + half_x + 3.8 < origin_x or
            site[0] - half_x - 3.8 > origin_x + size_x or
            site[1] + half_z + 3.8 < origin_z or
            site[1] - half_z - 3.8 > origin_z + size_z):
            continue
        for nx, nz, width in trees:
            x = origin_x + nx * size_x
            z = origin_z + nz * size_z
            margin = min(3.8, max(.5, width*1.5))
            if abs(x-site[0]) < half_x + margin and abs(z-site[1]) < half_z + margin:
                return True
    return False


def hits_compact(site, tiles):
    half_x, half_z = half_extents(site)
    for origin_x, origin_z, size_x, size_z, trees in tiles:
        if (site[0] + half_x + 3.8 < origin_x or
            site[0] - half_x - 3.8 > origin_x + size_x or
            site[1] + half_z + 3.8 < origin_z or
            site[1] - half_z - 3.8 > origin_z + size_z):
            continue
        # Store only three world-space floats, mirroring TreeClearanceSample.
        samples = [(origin_x + nx*size_x, origin_z + nz*size_z,
                    min(3.8, max(.5, width*1.5))) for nx,nz,width in trees]
        if any(abs(x-site[0]) < half_x+m and abs(z-site[1]) < half_z+m
               for x,z,m in samples):
            return True
    return False


class TreeClearanceStreaming(unittest.TestCase):
    def test_native_array_allocation_removed_and_each_tree_preserved(self):
        self.assertIn("data.GetTreeInstance(i)", SOURCE)
        self.assertIn("int treeCount = data.treeInstanceCount;", SOURCE)
        self.assertIn("for (int i = 0; i < treeCount; i++)", SOURCE)
        self.assertNotIn("data.treeInstances", SOURCE)
        self.assertIn("samples.Add(x, z, margin);", SOURCE)
        self.assertIn("Dictionary<Terrain, AarisTreeClearanceIndex>", SOURCE)
        self.assertIn("trees.Blocks(site.x, site.z, hx, hz)", SOURCE)

    def test_android_has_bounded_sampling_work_and_cleanup(self):
        self.assertIn("AndroidTreeProbeBudgetPerFrame = 256", SOURCE)
        self.assertIn("if (mobile && (++processed >= AndroidTreeProbeBudgetPerFrame ||", SOURCE)
        self.assertIn("AndroidTreeProbeBudgetSeconds = 0.001", SOURCE)
        self.assertIn("Time.realtimeSinceStartupAsDouble >= sliceDeadline", SOURCE)
        self.assertIn("processed = 0;", SOURCE)
        self.assertIn("yield return null;", SOURCE)
        self.assertIn("finally", SOURCE)
        self.assertIn("treeCache.Clear();", SOURCE)

    def test_original_exact_margin_tile_seam_and_relocation_paths(self):
        self.assertIn("Mathf.Min(3.8f,", SOURCE)
        self.assertIn("Mathf.Max(0.5f, tree.widthScale * 1.5f)", SOURCE)
        self.assertIn("FootprintHalfExtents(site, VegetationMargin", SOURCE)
        self.assertIn("foreach (Terrain terrain in all)", SOURCE)
        self.assertIn("TryRelocateInteractiveSite(site, terrains, out y)", SOURCE)
        self.assertIn("Math.Abs(tree.x - x) < halfX + tree.margin", INDEX)
        self.assertIn("Math.Abs(tree.z - z) < halfZ + tree.margin", INDEX)
        self.assertNotIn("trees.Length", INDEX + SOURCE)

    def test_bounded_compact_cache_matches_original_blocking_predicate(self):
        rng = random.Random(20261119)
        tiles = []
        for ox, oz in [(-1000,-1000),(0,-1000),(-1000,0),(0,0)]:
            points = [(rng.random(),rng.random(),rng.uniform(.3,3.5))
                      for _ in range(275)]
            # Include trees exactly on a tile seam and near rotated footprints.
            points += [((0-ox)/1000,(-20-oz)/1000, 2),
                       ((10-ox)/1000,(10-oz)/1000,1)]
            tiles.append((ox,oz,1000,1000,points))
        for _ in range(80):
            x,z = rng.uniform(-980,980),rng.uniform(-980,980)
            site = (x,z,rng.uniform(8,50),rng.uniform(8,50),
                    rng.choice((0,45,90,135)))
            for dx,dz in [(0,0),(18,0),(0,18),(-18,-18),(0,26)]:
                moved = (site[0]+dx,site[1]+dz,*site[2:])
                self.assertEqual(hits_native(moved,tiles),
                                 hits_compact(moved,tiles), moved)


if __name__ == "__main__":
    unittest.main(verbosity=2)
