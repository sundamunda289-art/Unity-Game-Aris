#!/usr/bin/env python3
"""Floor weapon pickups must honor unloaded solid walls, including bot scavenging."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
LOOT = (ROOT / "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs").read_text()
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs").read_text()
COVER = (ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text()
QA = (ROOT / "Assets/AarisWorld/Editor/AarisBotCombatIntegrationQA.cs").read_text()

class StreamedWallFloorLootContracts(unittest.TestCase):
    def test_shared_cover_index_is_cached_at_world_boot(self):
        boot = LOOT.split("internal static void EnsureForPlayer(", 1)[1].split(
            "private void Start()", 1)[0]
        self.assertIn("world.playerMotor = motor;", boot)
        self.assertIn("private AarisThirdPersonPlayer playerMotor;", LOOT)
        self.assertIn("internal AarisNearbyColliders CombatCover => nearbyColliders;", MOTOR)

    def test_stance_eye_and_physx_ray_reused_by_disabled_static_cover(self):
        path = LOOT.split("private bool HasClearPickupPath(", 1)[1].split(
            "private AarisDroppedWeapon FindDrop(", 1)[0]
        for token in (
            "body.center.y - body.height * .5f",
            "body.height * .65f",
            "Ray sightLine = new Ray(eye, toTarget / distance)",
            "Physics.RaycastNonAlloc(sightLine, pickupObstructions, distance",
            "if (collider == null || collider.transform.IsChildOf(looter)) continue;",
            "player.TryGetComponent(out playerMotor)",
            "playerMotor.CombatCover",
            "!cover.TryGetInactiveCoverHit(sightLine, distance, out _)",
        ):
            self.assertIn(token, path)
        self.assertNotIn("new GameObject(", path)
        self.assertNotIn("Physics.Raycast(", path)
        self.assertEqual(path.count("Physics.RaycastNonAlloc("), 1)

    def test_real_player_and_bot_use_same_reachability_gate(self):
        self.assertIn("HasClearPickupPath(player, position)", LOOT)
        take = LOOT.split("internal bool TryTakeForRival(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertLess(take.index("!HasClearPickupPath(rival, pickup.transform.position)"),
                        take.index("pickup.TryReserve()"))
        self.assertLess(take.index("!HasClearPickupPath(rival, pickup.transform.position)"),
                        take.index("pickup.CommitConsumed()"))

    def test_near_wall_probe_retains_collider_disabled_checks(self):
        self.assertIn("internal bool TryGetInactiveCoverHit(Ray ray, float distance, out float closest)", COVER)
        self.assertIn("if (!includeActive && distance > 3f && query > 0", COVER)

    def test_native_fixture_covers_live_streamed_reenabled_and_stances(self):
        native = QA.split("private static void VerifyStreamedGroundLootPickup(", 1)[1].split(
            "private static void VerifyRivalCameraWake(", 1)[0]
        for required in (
            '"Open ground-gun lanes allow player and rival pickups"',
            '"Floor-loot world caches the real player motor without Find"',
            '"Live house divider blocks both floor-gun pickup paths"',
            'Set(cover, "query", 1)',
            '((bool[])Get(cover, "disabledByStreaming"))[0] = true;',
            'float[] stanceHeights = { 1.8f, 1.2f, .5f };',
            '"Streamed house blocks player and rival gun pickup at stance height="',
            '"Re-enabled wall still blocks both physical pickups"',
            '"Removing house divider restores both pickups"',
        ):
            self.assertIn(required, native)
        self.assertIn("VerifyStreamedGroundLootPickup(report);", QA)
        self.assertIn("public static void RunStreamedGroundLootQA()", QA)

if __name__ == "__main__":
    unittest.main()
