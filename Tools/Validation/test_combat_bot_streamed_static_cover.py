#!/usr/bin/env python3
"""Source-level tests; real disabled-wall physics is in Editor native QA."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
DIRECTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
STREAMER = (ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text()
NATIVE = (ROOT / "Assets/AarisWorld/Editor/AarisBotCombatIntegrationQA.cs").read_text()

class RivalPhysicalCoverAuthority(unittest.TestCase):
    def test_existing_index_is_shared_with_real_bot_shots(self):
        entry = DIRECTOR.split("internal bool HasInactiveRivalShotCover(", 1)[1].split(
            "internal bool IsVirtualShotBlocked(", 1)[0]
        self.assertIn("nearbyColliders.TryGetInactiveCoverHit(ray, distance, out _)", entry)
        self.assertNotIn("Physics.Raycast", entry)
        self.assertNotIn("new ", entry)
        self.assertIn("internal bool TryGetInactiveCoverHit(Ray ray, float distance", STREAMER)
        self.assertIn("includeActive: false", STREAMER)

    def test_both_real_bot_hit_paths_require_streamed_cover_clearance(self):
        sight = DIRECTOR.split("private bool TryGetSightHit(", 1)[1].split(
            "private bool MuzzlePathClear(", 1)[0]
        self.assertEqual(sight.count("!owner.HasInactiveRivalShotCover(ray,"), 2)
        self.assertIn("nearestHit.distance", sight)
        self.assertIn("shotHit.distance", sight)
        self.assertIn("return firstCollider.transform.IsChildOf(victim) &&", sight)
        self.assertIn("return closest != null && closest.transform.IsChildOf(victim) &&", sight)

    def test_short_muzzle_segment_rejects_disabled_cover_too(self):
        muzzle = DIRECTOR.split("private bool MuzzlePathClear(", 1)[1].split(
            "private void OnDamaged(", 1)[0]
        self.assertIn("!owner.HasInactiveRivalShotCover(ray, .48f)", muzzle)
        self.assertIn("QueryTriggerInteraction.Ignore", muzzle)
        self.assertNotIn("new RaycastHit[", muzzle)

    def test_no_remote_simulation_damage_policy_regression(self):
        virtual = DIRECTOR.split("private void TickVirtualCombat(", 1)[1].split(
            "private float NextVirtualRandom(", 1)[0]
        self.assertIn("owner.IsVirtualShotBlocked(selfPosition, targetPosition)", virtual)
        self.assertIn("targetHealth.ApplyDamage(damage, gameObject);", virtual)
        self.assertIn("if (!cachedVirtualDamageRival.virtualBody)", virtual)
        self.assertIn("owner.MayAttackPlayer(this)", DIRECTOR)

    def test_native_qa_covers_disabled_wall_and_reacquisition(self):
        section = NATIVE.split("private static void VerifyStreamedOutBotLineOfSight(", 1)[1].split(
            "private static void VerifyRivalCameraWake(", 1)[0]
        for phrase in (
            'Call(rival, "HasSightTo", player.transform)',
            'Call(rival, "TryGetSightHit", ray)',
            '((bool[])Get(streamer, "disabledByStreaming"))[0] = true',
            '((bool[])Get(streamer, "disabledByStreaming"))[0] = false',
            '"Streamed-out wall blocks real bot tactical sight"',
            '"Bot reacquires player after wall removal"',
        ):
            self.assertIn(phrase, section)
        self.assertIn("VerifyStreamedOutBotLineOfSight(report);", NATIVE)
        self.assertIn("public static void RunStreamedBotCoverQA()", NATIVE)

if __name__ == "__main__":
    unittest.main()
