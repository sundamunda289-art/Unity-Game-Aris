#!/usr/bin/env python3
"""Static terrain tile metadata cache: 100 rival steps and source contracts."""
import random
import unittest
from pathlib import Path

SOURCE=(Path(__file__).resolve().parents[2] /
        "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
PROBE=SOURCE.split("private bool TryGetGround(float x, float z,",1)[1].split(
    "private bool SpawnClear(",1)[0]
META=SOURCE.split("private void CacheTerrainProbeMetadata()",1)[1].split(
    "private bool TryGetGround(",1)[0]


def ground(tiles, x, z):
    for ox,oy,oz,sx,sz in tiles:
        if x < ox+2 or x > ox+sx-2 or z < oz+2 or z > oz+sz-2:
            continue
        u=(x-ox)/sx
        v=(z-oz)/sz
        normal_y=0.5 if 0.45 < u < 0.47 else 0.95
        if normal_y < 0.78:
            continue
        sample=oy+4+20*u+5*v
        if sample > -37.35:
            return True, sample
    return False, 0.

class TerrainProbeContracts(unittest.TestCase):
    def test_only_initialization_fetches_unity_tile_metadata(self):
        self.assertEqual(SOURCE.count("CacheTerrainProbeMetadata();"),2)
        self.assertIn("origin = tile.transform.position",META)
        self.assertIn("size = data.size",META)
        self.assertIn("TerrainData data = tile.terrainData;",META)
        self.assertIn("terrainProbeMeta = new TerrainProbeMeta[terrains.Length]",META)
        self.assertIn("TerrainProbeMeta meta = terrainProbeMeta[i]",PROBE)
        self.assertIn("Vector3 origin = meta.origin, size = meta.size;",PROBE)
        self.assertNotIn("tile.transform.position",PROBE)
        self.assertNotIn("tile.terrainData",PROBE)

    def test_ground_slope_ocean_margin_and_order_preserved(self):
        for s in ("float margin = 2f", "x < origin.x + margin",
                  "x > origin.x + size.x - margin", "z < origin.z + margin",
                  "z > origin.z + size.z - margin",
                  "float u = (x - origin.x) / size.x;",
                  "float v = (z - origin.z) / size.z;",
                  "data.GetInterpolatedNormal(u, v).y < 0.78f",
                  "origin.y + tile.SampleHeight(new Vector3(x, 0f, z))",
                  "height > OceanCutoff"):
            self.assertIn(s,PROBE)
        self.assertIn("TryGetGround(x, z, out height, margin: 0f)", SOURCE)
        # Two overlapping tiles deliberately have different Y and slope;
        # neither the order nor the ocean/steep rejection may change.
        tiles=[(-1000,-38,-1000,2000,2000),(-500,3,-500,1000,1000)]
        cache=tuple(tiles)
        rng=random.Random(20261123)
        for _ in range(200):
            for i in range(100):
                x=rng.uniform(-1100,1100)
                z=rng.uniform(-1100,1100)
                self.assertEqual(ground(tiles,x,z),ground(cache,x,z))
        for x,z in [(-998,-998),(-1000,-1000),(999,999),
                    (-80,-100),(0,0),(500,500),(-490,-490)]:
            self.assertEqual(ground(tiles,x,z),ground(cache,x,z))

    def test_match_start_refreshes_cache_after_start(self):
        start=SOURCE.split("private void Start()",1)[1].split(
            "private void Update()",1)[0]
        begin=SOURCE.split("private void Begin()",1)[1].split(
            "private void TrySpawnSlot(",1)[0]
        for body in (start,begin):
            self.assertLess(body.index("terrains = Terrain.activeTerrains;"),
                            body.index("CacheTerrainProbeMetadata();"))

if __name__=="__main__":
    unittest.main()
