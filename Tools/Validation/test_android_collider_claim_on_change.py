#!/usr/bin/env python3
"""Deterministic no-op collider refresh admission contracts for budget Android.

Source + small scheduling model only; not native Unity PhysX or phone FPS evidence.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
COLLIDERS = (SRC / "AarisNearbyColliders.cs").read_text(encoding="utf-8")
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text(encoding="utf-8")


def claims_for_refresh(force, was_enabled, wanted, pending_retire=False):
    """Mirror claimed-frame policy without modeling the native physics engine."""
    if force:
        return 1  # Physics.SyncTransforms must retain original native protection.
    if wanted and not was_enabled:
        return 1  # A real collider enable.
    return 0  # Ordinary grid read or queued *future* native retirement.


class ColliderClaimOnChange(unittest.TestCase):
    def setUp(self):
        self.refresh = COLLIDERS.split(
            "private void RefreshNow(bool forcePhysicsSync)", 1
        )[1].split("private void FlushPendingColliderReleases()", 1)[0]

    def test_forced_teleport_refresh_keeps_original_native_frame_fence(self):
        body = self.refresh
        forced_guard = body.index("if (forcePhysicsSync)\n")
        first_claim = body.index(
            "AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();"
        )
        self.assertLess(forced_guard, first_claim)
        self.assertLess(first_claim, body.index("lastPosition = target.position;"))
        self.assertIn("if (forcePhysicsSync) Physics.SyncTransforms();", body)
        self.assertEqual(claims_for_refresh(force=True, was_enabled=True, wanted=True), 1)
        self.assertEqual(claims_for_refresh(force=True, was_enabled=False, wanted=True), 1)

    def test_movement_gate_only_if_colliders_really_transition(self):
        body = self.refresh
        self.assertEqual(body.count("if (!c.enabled)"), 2)
        self.assertEqual(body.count("c.enabled = true;"), 2)
        self.assertEqual(body.count(
            "AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();"
        ), 3)
        self.assertIn("if (forcePhysicsSync)", body)
        self.assertLess(body.index("if (forcePhysicsSync)"), body.index("lastPosition = target.position;"))
        for block in body.split("if (!c.enabled)")[1:]:
            claim = block.index("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();")
            enable = block.index("c.enabled = true;")
            self.assertLess(claim, enable)
        self.assertEqual(claims_for_refresh(False, True, True), 0)
        self.assertEqual(claims_for_refresh(False, False, True), 1)

    def test_deferred_retirement_does_not_claim_until_native_batch(self):
        refresh = self.refresh
        deferred = COLLIDERS.split(
            "private IEnumerator ReleaseDistantAcrossFrames()", 1
        )[1].split("internal bool HasPotentialInactiveObstacle(", 1)[0]
        self.assertIn("pendingColliderRelease.Add(i);", refresh)
        self.assertIn("StartCoroutine(ReleaseDistantAcrossFrames());", refresh)
        self.assertIn("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();", deferred)
        self.assertLess(
            deferred.index("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();"),
            deferred.index("collider.enabled = false;"))
        self.assertEqual(claims_for_refresh(False, True, False, pending_retire=True), 0)

    def test_no_change_to_distance_coverage_and_visuals(self):
        for token in (
            "private const float ColliderReleaseMargin = 6f;",
            "private const float RefreshTravelSquared = 9f;",
            "private const int AndroidReleaseUpdatesPerFrame = 12;",
            "float enterLimit = enterDistance * enterDistance;",
            "float retainLimit = retainDistance * retainDistance;",
            "float limit = c.enabled ? retainLimit : enterLimit;",
            "if (forcePhysicsSync) Physics.SyncTransforms();",
        ):
            self.assertIn(token, COLLIDERS)
        for token in ("treeDistance =", "renderScale =", "QualitySettings.",
                      "Destroy(", "Resources.Load"):
            self.assertNotIn(token, self.refresh)
        self.assertIn("if (IsConstrainedDevice())", GATE)
        self.assertIn("claimedFrame = Time.frameCount;", GATE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
