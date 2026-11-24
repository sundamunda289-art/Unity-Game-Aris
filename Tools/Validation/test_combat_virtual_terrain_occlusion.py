#!/usr/bin/env python3
"""Source-level contracts; native terrain/height accuracy checked in Unity QA."""
from pathlib import Path
import math
import unittest

ROOT = Path(__file__).resolve().parents[2]
DIRECTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
EDITOR = (ROOT / "Assets/AarisWorld/Editor/AarisBotCombatIntegrationQA.cs").read_text()

def segment_hits_ridge(length, ridge_start, ridge_end, height, eye_height=1.28):
    segments = min(16, max(1, math.ceil(length / 3.0)))
    return any(
        ridge_start <= length * step / segments <= ridge_end
        and height > eye_height - .21 * step / segments + .12
        for step in range(1, segments)
    )

class VirtualTerrainCoverContracts(unittest.TestCase):
    def test_virtual_shots_check_sculpted_terrain_as_well_as_streamed_buildings(self):
        shot = DIRECTOR.split("internal bool IsVirtualShotBlocked(", 1)[1].split(
            "private void EvaluateZone(", 1)[0]
        self.assertIn("nearbyColliders.HasStreamedShotCover(", shot)
        self.assertIn("return HasTerrainCoverOnVirtualShot(origin, path, length);", shot)
        self.assertIn("if (length <= 0.001f) return true;", shot)

    def test_terrain_probe_is_bounded_and_has_no_physics_or_dynamic_allocation(self):
        probe = DIRECTOR.split("private bool HasTerrainCoverOnVirtualShot(", 1)[1].split(
            "internal bool IsVirtualShotBlocked(", 1)[0]
        for required in (
            "Mathf.CeilToInt(length / 3f)", "1, 16", "step < segments",
            "meta.data.GetInterpolatedHeight(u, v)",
            "groundY > shotY + 0.12f", "meta.origin.y",
        ):
            self.assertIn(required, probe)
        for forbidden in ("Physics.Raycast", "Physics.Linecast", "new GameObject",
                          "new float[", "GetInterpolatedNormal", "GetHeights("):
            self.assertNotIn(forbidden, probe)
        self.assertEqual(DIRECTOR.count("HasTerrainCoverOnVirtualShot("), 2)

    def test_authored_virtual_damage_is_gated(self):
        virtual = DIRECTOR.split("private void TickVirtualCombat(", 1)[1].split(
            "private float NextVirtualRandom(", 1)[0]
        self.assertLess(virtual.index("owner.IsVirtualShotBlocked("),
                        virtual.index("targetHealth.ApplyDamage("))
        self.assertIn("!cachedVirtualDamageRival.virtualBody", virtual)
        self.assertIn("FarSimulationDistance * FarSimulationDistance", virtual)

    def test_arithmetic_ridge_and_clear_air(self):
        self.assertTrue(segment_hits_ridge(32, 14, 18, 4))
        self.assertFalse(segment_hits_ridge(32, 14, 18, 0))
        self.assertFalse(segment_hits_ridge(32, 14, 18, 4, eye_height=7.28))

    def test_native_qa_observes_real_unloaded_terrain_and_elevated_shots(self):
        section = EDITOR.split("private static void VerifyVirtualTerrainShotCover(", 1)[1].split(
            "private static void VerifyVirtualCombat(", 1)[0]
        for expected in (
            "Terrain.CreateTerrainGameObject(data)", "data.SetHeights(0, 0, heights)",
            "terrainCollider.enabled = false",
            '"Three-metre sampled terrain ridge blocks distant bot damage"',
            '"Virtual terrain obstruction works in both firing directions"',
            '"Height clearance allows a genuinely elevated shot"',
            '"Distant virtual shot resumes after terrain ridge removal"',
        ):
            self.assertIn(expected, section)
        self.assertIn("VerifyVirtualTerrainShotCover(report);", EDITOR)
        self.assertIn("public static void RunVirtualTerrainCoverQA()", EDITOR)

if __name__ == "__main__":
    unittest.main()
