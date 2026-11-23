#!/usr/bin/env python3
"""Synthetic 100-bot CPU QA must initialize the real terrain-probe cache."""
import unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
BENCH=(ROOT/"Assets/AarisWorld/Editor/AarisBotFrameBenchmark.cs").read_text()
AI=(ROOT/"Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
SETUP=BENCH.split("private static void Begin()",1)[1].split(
    "private static void SpawnRivals()",1)[0]
PROBE=AI.split("private bool TryGetGround(float x, float z,",1)[1].split(
    "private bool SpawnClear(",1)[0]

class BenchmarkTerrainMetadata(unittest.TestCase):
    def test_cache_initialized_after_synthetic_terrain_and_before_bots(self):
        for s in (
            'Set(director, "terrains", new Terrain[] { qaTerrain });',
            '"CacheTerrainProbeMetadata", Private);',
            "metadataSetup.Invoke(director, null);",
            "QA_TERRAIN_CACHE_INITIALIZER_MISSING",
            "terrainData.size = new Vector3(2600f, 10f, 2600f);"):
            self.assertIn(s,SETUP)
        self.assertLess(SETUP.index('Set(director, "terrains",'),
                        SETUP.index("metadataSetup.Invoke(director, null);"))
        self.assertLess(SETUP.index("metadataSetup.Invoke(director, null);"),
                        SETUP.index("botTransforms = new Transform[100];"))

    def test_same_ground_rules_for_production_and_qa(self):
        self.assertIn("TerrainProbeMeta meta = terrainProbeMeta[i];",PROBE)
        self.assertIn("data.GetInterpolatedNormal(u, v).y < 0.78f",PROBE)
        self.assertIn("origin.y + tile.SampleHeight(new Vector3(x, 0f, z))",PROBE)
        self.assertIn("height > OceanCutoff",PROBE)
        self.assertIn("CacheTerrainProbeMetadata();",AI)
        self.assertIn("owner.TryVirtualGround(next.x, next.z, out float ground)",AI)

    def test_100_bots_live_far_simulation_contract(self):
        for s in ("for (int i = 0; i < 100; i++)",
                  "100 lightweight rivals spawned",
                  "director.TickDormantRivals(Time.time)",
                  "REMOTE_BOTS_NOT_MOVING",
                  "moved != botTransforms.Length",
                  "virtualized < 90"):
            self.assertIn(s,BENCH)

if __name__=="__main__":
    unittest.main()
