#!/usr/bin/env python3
"""Static regression contract for Android native-collider / optional-import pacing.

The source checks guard scheduling and visual invariants; only Unity-on-Android
profiling can establish actual frame-time improvements.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets" / "AarisWorld" / "Scripts"
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text()
COLLIDERS = (SRC / "AarisNearbyColliders.cs").read_text()
RESERVE = "AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();"


class PhysicsStreamingFramePriority(unittest.TestCase):
    def test_android_only_physics_reservation(self):
        method = GATE.split("internal static void ReservePhysicsStreamingFrame()", 1)[1]
        method = method.split("internal static bool IsConstrainedDevice()", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", method)
        self.assertIn("if (IsConstrainedDevice())", method)
        self.assertIn("claimedFrame = Time.frameCount;", method)
        self.assertIn("#endif", method)
        self.assertNotIn("Resources.Load", method)
        self.assertNotIn("Instantiate(", method)
        self.assertNotIn("QualitySettings", method)
        self.assertNotIn("renderScale", method)
        self.assertNotIn("SetActive(", method)

    def test_same_frame_optional_import_respects_physics(self):
        optional = GATE.split("internal static bool TryClaimOptionalModelBuild()", 1)[1]
        optional = optional.split("internal static ResourceRequest TryStartOptionalLoadAsync", 1)[0]
        diagnostic = GATE.split("internal static bool TryClaimNonessentialDiagnosticFrame()", 1)[1]
        diagnostic = diagnostic.split("internal static bool TryClaimOptionalModelBuild()", 1)[0]
        self.assertIn("if (claimedFrame == frame)", optional)
        self.assertIn("SameFrameDeferredAttempts++;", optional)
        self.assertIn("if (claimedFrame == frame || importIntegrationFrame == frame)", diagnostic)
        self.assertIn("if (IsOptionalImportBusy()) return false;", diagnostic)
        self.assertIn("CleanupDeferredAttempts++;", optional)
        self.assertIn("if (IsEssentialRigFrame())", optional)

    def test_native_batches_reserve_current_frame_after_each_yield(self):
        index = COLLIDERS.split("private IEnumerator BuildGridAcrossFrames()", 1)[1].split(
            "private void IndexCollider(", 1)[0]
        deactivation = COLLIDERS.split(
            "private IEnumerator DeactivateDistantAcrossFrames()", 1
        )[1].split("private int Cell(", 1)[0]
        release = COLLIDERS.split(
            "private IEnumerator ReleaseDistantAcrossFrames()", 1
        )[1].split("internal bool HasPotentialInactiveObstacle(", 1)[0]
        for label, body in (
            ("index", index), ("deactivation", deactivation), ("release", release)
        ):
            self.assertGreaterEqual(body.count(RESERVE), 2, label)
            self.assertIn("yield return null;", body, label)
            self.assertIn("Time.realtimeSinceStartupAsDouble", body, label)

    def test_immediate_colliders_precede_optional_admissions(self):
        refresh = COLLIDERS.split("private void RefreshNow(bool forcePhysicsSync)", 1)[1]
        refresh = refresh.split("private void FlushPendingColliderReleases()", 1)[0]
        self.assertIn(RESERVE, refresh)
        self.assertLess(refresh.index(RESERVE), refresh.index("nextActive.Clear();"))
        self.assertIn("if (!c.enabled)", refresh)
        self.assertEqual(refresh.count("c.enabled = true;"), 2)
        guarded = refresh.split("if (!c.enabled)", 1)[1].split("c.enabled = true;", 1)[0]
        self.assertIn("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();", guarded)
        self.assertIn("ColliderReleaseMargin", refresh)
        self.assertIn("if (forcePhysicsSync) Physics.SyncTransforms();", refresh)

    def test_no_world_visual_or_physics_policy_change(self):
        self.assertIn("private const int MaxCellsPerCollider = 64;", COLLIDERS)
        self.assertIn("private const float RefreshTravelSquared = 9f;", COLLIDERS)
        self.assertIn("private const int AndroidReleaseUpdatesPerFrame = 12;", COLLIDERS)
        self.assertIn("private const int DeferredColliderUpdatesPerFrame = 24;", COLLIDERS)
        self.assertIn("private const int AndroidIndexUpdatesPerFrame = 96;", COLLIDERS)
        self.assertNotIn("SetDetailResolution", COLLIDERS)
        self.assertNotIn("treeDistance =", COLLIDERS)
        self.assertNotIn("renderScale =", COLLIDERS)

    def test_admission_simulation_does_not_drop_pending_model_work(self):
        # Simulates frame exclusivity only, NOT Unity GPU/CPU performance.
        claimed = {20: "physics", 21: "physics"}
        pending_optional = ["scene detail", "vehicle model", "landmark"]
        started = []
        for frame in range(20, 28):
            if frame in claimed:
                continue
            if pending_optional:
                started.append((frame, pending_optional.pop(0)))
                claimed[frame] = "optional"
        self.assertEqual([item for _, item in started],
                         ["scene detail", "vehicle model", "landmark"])
        self.assertGreaterEqual(started[0][0], 22)
        self.assertFalse(pending_optional)


if __name__ == "__main__":
    unittest.main(verbosity=2)
