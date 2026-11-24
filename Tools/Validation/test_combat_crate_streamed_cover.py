#!/usr/bin/env python3
"""Fast source contracts: the isolated real Unity PhysX case runs in native QA."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
CRATE = (ROOT / "Assets/AarisWorld/Scripts/AarisDeathCrateWorld.cs").read_text()
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs").read_text()
COVER = (ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text()
QA = (ROOT / "Assets/AarisWorld/Editor/AarisBotCombatIntegrationQA.cs").read_text()

class PickupStreamedCoverContracts(unittest.TestCase):
    def test_bind_caches_real_player_motor_once(self):
        bind = CRATE.split("internal void Bind(Transform target)", 1)[1].split(
            "internal void Spawn(", 1)[0]
        self.assertIn("playerMotor = target != null", bind)
        self.assertIn("target.GetComponent<AarisThirdPersonPlayer>()", bind)
        self.assertIn("internal AarisNearbyColliders CombatCover => nearbyColliders;", MOTOR)

    def test_exact_stance_eye_ray_shared_by_physx_and_disabled_cover(self):
        reach = CRATE.split("private bool Reachable(Vector3 from, Vector3 to)", 1)[1].split(
            "private Drop Nearest(", 1)[0]
        for required in (
            "playerCapsule.center.y", "playerCapsule.height * .65f",
            "Mathf.Clamp(footOffset +", "Vector3 origin = from + Vector3.up * rayLift",
            "Ray pickupRay = new Ray(origin, delta / distance)",
            "AarisPhysicsHitBuffer.Raycast(pickupRay,", "playerMotor.CombatCover",
            "!cover.TryGetInactiveCoverHit(pickupRay, distance, out _)",
        ):
            self.assertIn(required, reach)
        self.assertNotIn("Physics.Raycast(", reach)
        self.assertNotIn("new RaycastHit[", reach)

    def test_short_disabled_cover_probe_does_not_take_active_radius_shortcut(self):
        query = COVER.split("private bool TryGetStreamedCoverHit(", 1)[1].split(
            "private void ProbeStreamedCover(", 1)[0]
        self.assertIn("if (!includeActive && distance > 3f && query > 0", query)
        self.assertIn("ProbeStreamedCover(i, ray, includeActive, ref closest)", query)
        self.assertIn("disabledByStreaming[index]", COVER)

    def test_loot_and_hud_use_reachability_not_uncached_distance(self):
        near = CRATE.split("private Drop Nearest(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("if (mustReach && !Reachable(point, drop.position)) continue;", near)
        self.assertIn("instance.Nearest(seeker.position, PickupRadius,", CRATE)
        self.assertIn("cachedPickupNear = Nearest(here, PickupRadius, true, 0, true) != null;", CRATE)

    def test_native_qa_exercises_all_stances_and_fastpath(self):
        test = QA.split("private static void VerifyStreamedCratePickup(", 1)[1].split(
            "private static void VerifyRivalCameraWake(", 1)[0]
        for required in (
            '"Unobstructed two-metre crate is reachable"',
            '"Live wall blocks crate pickup"',
            'Set(cover, "query", 1)', 'Set(cover, "lastPosition", from)',
            '((bool[])Get(cover, "disabledByStreaming"))[0] = true',
            'float[] stances = { 1.8f, 1.2f, .5f }',
            '"Streamed wall denies crate at stance height="',
            '"Re-enabled wall still denies crate pickup"',
            '"Crate becomes reachable after divider removed"',
        ):
            self.assertIn(required, test)
        self.assertIn("public static void RunStreamedCratePickupQA()", QA)

if __name__ == "__main__":
    unittest.main()
