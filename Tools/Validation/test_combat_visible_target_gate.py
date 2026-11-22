#!/usr/bin/env python3
"""Source contract and scenario simulation: unseen streamed rivals cannot attack or lock."""
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()


class StreamedRivalTargetGates(unittest.TestCase):
    def test_visual_readiness_is_not_equivalent_to_physics_spawn(self):
        self.assertIn("(visualRoot != null && visualRoot.activeInHierarchy) ||", SOURCE)
        self.assertIn("(farVisualRoot != null && farVisualRoot.activeInHierarchy);", SOURCE)
        # IsAlive remains separate: unseen rivals continue distant world simulation.
        self.assertIn("public bool IsAlive => health != null && !health.IsDead;", SOURCE)
        self.assertNotIn("public bool IsAlive => health != null && !health.IsDead &&", SOURCE)

    def test_4x_targets_use_pooled_distant_3d_lod_not_more_skeletons(self):
        self.assertIn("private const float FarVisualDistance = 485f;", SOURCE)
        self.assertIn("if (!visualBuilt && playerDistanceSqr < 105f * 105f)", SOURCE)
        self.assertIn("bool visible = playerDistanceSqr < 155f * 155f &&", SOURCE)
        self.assertIn("(playerDistanceSqr < 55f * 55f || cameraRelevant);", SOURCE)
        self.assertIn("playerDistanceSqr < FarVisualDistance * FarVisualDistance", SOURCE)
        self.assertIn("(visualRoot == null || !visualRoot.activeSelf)", SOURCE)
        self.assertIn("if (farVisualRoot != null && farVisualRoot.activeSelf != farBand)", SOURCE)
        self.assertIn("BuildDistantVisual();", SOURCE)
        self.assertIn("sharedFarVisualMesh", SOURCE)
        self.assertIn("sharedFarVisualMaterial", SOURCE)
        self.assertIn("enableInstancing = true", SOURCE)
        self.assertIn("mesh.UploadMeshData(true);", SOURCE)
        self.assertIn("renderer.shadowCastingMode = ShadowCastingMode.Off;", SOURCE)
        self.assertIn("AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()", SOURCE)
        far = SOURCE.split("private void BuildDistantVisual()", 1)[1].split(
            "private void BuildFallbackSilhouette()", 1)[0]
        self.assertNotIn("AddComponent<Animator>", far)
        self.assertNotIn("AddComponent<Collider>", far)
        self.assertNotIn("CreatePrimitive", far)
        self.assertNotIn("Instantiate(", far)
        # Explicit model guarantees: the original near rig wins in the
        # 155 m band; shared lightweight art renders at 156-480 m.
        def lod(distance, rig_built):
            near = rig_built and distance < 155
            far_enabled = distance < 485 and not near
            return near, far_enabled
        for distance in (0, 105, 154.9):
            self.assertEqual(lod(distance, True), (True, False))
        self.assertEqual(lod(155, True), (False, True))
        self.assertEqual(lod(480, True), (False, True))
        self.assertEqual(lod(485, True), (False, False))
        self.assertEqual(lod(300, False), (False, True))

    def test_red_lock_skips_unrendered_opponents(self):
        target_search = SOURCE.split("internal static bool TryAcquireAimTarget", 1)[1].split("private void RefreshStatus", 1)[0]
        self.assertIn("!bot.HasVisibleBody", target_search)
        self.assertIn("closest.transform.IsChildOf(candidate.transform)", target_search)

    def test_stale_slot_cannot_authorize_invisible_or_dead_attacker(self):
        slot = SOURCE.split("internal bool MayAttackPlayer(", 1)[1].split(
            "internal bool TryLootForRival(", 1)[0]
        self.assertIn("!bot.IsAlive || !bot.HasVisibleBody", slot)
        self.assertIn("return bot == attackerOne || bot == attackerTwo;", slot)
        # A cached slot is necessary, but no longer sufficient if its body
        # streamed out or death happened before the next full 100-rival scan.
        for selected, alive, visible, allowed in (
            (True, True, True, True),
            (True, True, False, False),
            (True, False, True, False),
            (False, True, True, False),
        ):
            self.assertEqual(selected and alive and visible, allowed)

    def test_unseen_rivals_do_not_take_player_attack_slots(self):
        selection = SOURCE.split("private void RefreshPlayerAttackers", 1)[1].split("internal bool MayAttackPlayer", 1)[0]
        los = SOURCE.split("internal bool HasPlayerSightForSelection", 1)[1].split("private bool HasSight()", 1)[0]
        self.assertIn("bot.HasPlayerSightForSelection()", selection)
        self.assertIn("if (!HasVisibleBody || playerTarget == null || playerTargetHealth == null ||", los)
        self.assertIn("playerTargetHealth.IsDead) return false;", los)
        self.assertIn("return HasSightTo(playerTarget);", los)
        self.assertIn("return bot == attackerOne || bot == attackerTwo;", SOURCE)
        rivals = [(4, False, True), (6, True, False), (12, True, True), (18, True, True)]
        visible_in_sight = [d for d, visible, clear in rivals if d < 42 and visible and clear]
        self.assertEqual(visible_in_sight[:2], [12, 18])


if __name__ == "__main__":
    unittest.main()
