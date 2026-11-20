#!/usr/bin/env python3
"""Android deferred collider release: no-op frames do not consume model slots.

Source and small scheduling model only; not an actual Android physics/FPS trace.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text(
    encoding="utf-8")
GATE = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileStreamingFrameGate.cs").read_text(
    encoding="utf-8")
BODY = SOURCE.split("private IEnumerator ReleaseDistantAcrossFrames()", 1)[1].split(
    "// Parked cars can stream", 1)[0]
RESERVE = "AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();"


def admission_for_queued_release(changed):
    """One native frame claim iff any queued collider actually transitions."""
    claimed = False
    calls = 0
    for should_disable in changed:
        if should_disable and not claimed:
            claimed = True
            calls += 1
    return calls


class ColliderReleaseNoopAdmission(unittest.TestCase):
    def test_admission_is_inside_real_disable_branch_and_before_toggle(self):
        self.assertEqual(BODY.count(RESERVE), 1)
        condition = "if (dx * dx + dz * dz > retainSquared)"
        self.assertIn(condition, BODY)
        self.assertIn("if (!reservedReleaseFrame)", BODY)
        self.assertLess(BODY.index(condition), BODY.index("if (!reservedReleaseFrame)"))
        self.assertLess(BODY.index("if (!reservedReleaseFrame)"), BODY.index(RESERVE))
        self.assertLess(BODY.index(RESERVE), BODY.index("collider.enabled = false;"))
        self.assertIn("reservedReleaseFrame = true;", BODY)

    def test_initial_yield_and_each_subsequent_frame_reset(self):
        self.assertIn("yield return null;\n            // A stale retirement queue", BODY)
        self.assertIn("bool reservedReleaseFrame = false;", BODY)
        self.assertIn("yield return null;\n                    framePosition = target != null ? target.position : default(Vector3);", BODY)
        self.assertIn("retainSquared = retain * retain;", BODY)
        self.assertLess(
            BODY.index("framePosition = target != null ? target.position",
                       BODY.index("while (colliderReleaseCursor")),
            BODY.index("reservedReleaseFrame = false;",
                       BODY.index("while (colliderReleaseCursor"))
        )
        self.assertEqual(BODY.count("target.position"), 2)
        self.assertIn("int processed = 0;", BODY)
        self.assertIn("processed >= AndroidReleaseUpdatesPerFrame", BODY)
        self.assertIn("Time.realtimeSinceStartupAsDouble - frameStartedAt", BODY)

    def test_live_authority_and_teleport_safety_unchanged(self):
        for token in ("colliderReleaseQueued[i] = false;", "wanted[i] != query",
                      "target.position", "ColliderReleaseMargin",
                      "colliderReleaseCursor < pendingColliderRelease.Count",
                      "pendingColliderRelease.Clear();",
                      "deferredColliderRelease = null;"):
            self.assertIn(token, BODY)
        self.assertIn("private const int AndroidReleaseUpdatesPerFrame = 12;", SOURCE)
        self.assertIn("private const float ColliderReleaseMargin = 6f;", SOURCE)
        self.assertIn("if (forcePhysicsSync) Physics.SyncTransforms();", SOURCE)
        self.assertIn("private void FlushPendingColliderReleases()", SOURCE)
        for forbidden in ("treeDistance =", "renderScale =", "SetDetailResolution(",
                          "Object.Destroy(", "Resources.UnloadUnusedAssets()"):
            self.assertNotIn(forbidden, BODY)

    def test_noop_reentry_and_real_physics_model(self):
        for decisions, expected in [
            ([], 0), ([False], 0), ([False] * 40, 0),
            ([True], 1), ([False, True, True], 1),
            ([True, False, True, False], 1),
        ]:
            self.assertEqual(admission_for_queued_release(decisions), expected)
        # A 20-object native release spread across 12/8 items claims once
        # in each changed frame; a fully stale batch claims neither.
        self.assertEqual(sum(admission_for_queued_release(part)
                             for part in ([True] * 12, [True] * 8)), 2)
        self.assertEqual(sum(admission_for_queued_release(part)
                             for part in ([False] * 12, [False] * 8)), 0)
        self.assertIn("if (IsConstrainedDevice())", GATE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
