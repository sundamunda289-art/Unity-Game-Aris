#!/usr/bin/env python3
"""Verify collision mesh pre-baking is enabled without changing runtime physics.

This is a saved-project static regression, not a real player build, physics
timing test or assurance that dynamically constructed MeshColliders are baked.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
PLAYER = (ROOT / "ProjectSettings/ProjectSettings.asset").read_text()
TIME = (ROOT / "ProjectSettings/TimeManager.asset").read_text()
SCENE = (ROOT / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity").read_text()


class AndroidPrebakedCollisionMeshes(unittest.TestCase):
    def test_player_build_prebakes_mesh_collider_cooking(self):
        found = re.findall(r"(?m)^  bakeCollisionMeshes: ([01])$", PLAYER)
        self.assertEqual(found, ["1"], "Player collision-mesh prebake must be enabled")

    def test_saved_native_world_still_contains_mesh_colliders(self):
        self.assertGreaterEqual(SCENE.count("MeshCollider:"), 90)
        self.assertIn("m_Mesh:", SCENE)

    def test_fixed_step_not_cut_to_change_collision_motion(self):
        self.assertIn("Maximum Allowed Timestep: 0.33333334", TIME)
        self.assertIn("m_Numerator: 141120000", TIME)
        self.assertIn("m_Count: 2822399", TIME)


if __name__ == "__main__":
    unittest.main(verbosity=2)
