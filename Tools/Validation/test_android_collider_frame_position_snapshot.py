#!/usr/bin/env python3
"""Native Transform read pacing for Android collider streaming.

This is a source regression check, not Unity Editor or Android FPS evidence.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text(
    encoding="utf-8"
)

def section(start, end):
    return SOURCE.split(start, 1)[1].split(end, 1)[0]


class ColliderFrameSnapshot(unittest.TestCase):
    def test_startup_deactivation_uses_one_position_read_per_batch(self):
        body = section(
            "private IEnumerator DeactivateDistantAcrossFrames()",
            "private int Cell(",
        )
        first_loop = body.index("for (int i = 0; i < obstacles.Length; i++)")
        refresh = body.index("framePosition = target != null ? target.position",
                             first_loop)
        self.assertEqual(body.count("target.position"), 2)
        self.assertLess(body.index("Vector3 framePosition = target"), first_loop)
        self.assertLess(body.index("float safeDistanceSquared"), first_loop)
        self.assertLess(body.index("yield return null;", first_loop), refresh)
        self.assertNotIn("target.position", body[first_loop:refresh])
        self.assertIn("if (target == null)", body)
        self.assertIn("if (dx * dx + dz * dz > safeDistanceSquared)", body)
        self.assertIn("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();", body)

    def test_movement_release_refreshes_position_after_each_yield(self):
        body = section(
            "private IEnumerator ReleaseDistantAcrossFrames()",
            "internal bool HasPotentialInactiveObstacle(",
        )
        loop = body.index("while (colliderReleaseCursor < pendingColliderRelease.Count)")
        refresh = body.index("framePosition = target != null ? target.position", loop)
        self.assertEqual(body.count("target.position"), 2)
        self.assertLess(body.index("Vector3 framePosition = target"), loop)
        self.assertLess(body.index("float retainSquared"), loop)
        self.assertLess(body.index("yield return null;", loop), refresh)
        self.assertNotIn("target.position", body[loop:refresh])
        self.assertIn("wanted[i] != query", body)
        self.assertIn("if (dx * dx + dz * dz > retainSquared)", body)
        self.assertIn("if (!reservedReleaseFrame)", body)
        self.assertLess(
            body.index("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();"),
            body.index("collider.enabled = false;")
        )

    def test_no_visual_or_collision_policy_changes(self):
        for token in (
            "private const float ColliderReleaseMargin = 6f;",
            "private const float RefreshTravelSquared = 9f;",
            "private const int AndroidReleaseUpdatesPerFrame = 12;",
            "private const int DeferredColliderUpdatesPerFrame = 24;",
            "private const int AndroidIndexUpdatesPerFrame = 96;",
            "private const double AndroidReleaseFrameBudgetSeconds = 0.001;",
            "private const double AndroidDeactivationBudgetSeconds = 0.0015;",
            "if (forcePhysicsSync) Physics.SyncTransforms();",
        ):
            self.assertIn(token, SOURCE)
        for token in ("treeDistance =", "renderScale =", "SetActive(",
                      "new Material(", "Instantiate("):
            self.assertNotIn(token, SOURCE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
