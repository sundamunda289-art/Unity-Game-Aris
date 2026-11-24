#!/usr/bin/env python3
"""Android collider release burst: static source and deterministic queue model.

No real Unity PhysX timing / phone FPS claim is made.
"""
import unittest
from pathlib import Path

CODE = (Path(__file__).resolve().parents[2] /
        "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text()

def part(start, end):
    return CODE.split(start, 1)[1].split(end, 1)[0]


class ReleaseModel:
    """Small functional mirror of queued retirements for race-edge assertions."""
    def __init__(self, n=50):
        self.enabled = [True] * n
        self.pending = []
        self.queued = [False] * n
        self.wanted = set()
        self.player_near = set()
    def refresh(self, wanted, teleport=False):
        self.wanted = set(wanted)
        self.player_near = set(wanted)
        for i in self.wanted:
            self.enabled[i] = True
        for i in range(len(self.enabled)):
            if i not in self.wanted and self.enabled[i]:
                if teleport:
                    self.enabled[i] = False
                elif not self.queued[i]:
                    self.pending.append(i)
                    self.queued[i] = True
        if teleport:
            self.flush()
    def step(self, budget=12):
        ran = 0
        while self.pending and ran < budget:
            i = self.pending.pop(0)
            self.queued[i] = False
            if i not in self.wanted and i not in self.player_near:
                self.enabled[i] = False
            ran += 1
        return ran
    def flush(self):
        while self.pending:
            i = self.pending.pop(0)
            self.queued[i] = False
            if i not in self.wanted:
                self.enabled[i] = False


class AndroidColliderReleaseBudget(unittest.TestCase):
    def test_source_only_defers_movement_not_nearby_enables_or_teleport(self):
        refresh = part("private void RefreshNow(bool forcePhysicsSync)", "private void FlushPendingColliderReleases()")
        self.assertIn("if (!c.enabled)", refresh)
        self.assertEqual(refresh.count("c.enabled = true;"), 2)
        guarded = refresh.split("if (!c.enabled)", 1)[1].split("c.enabled = true;", 1)[0]
        self.assertIn("AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame();", guarded)
        self.assertIn("Application.platform == RuntimePlatform.Android", refresh)
        self.assertIn("!forcePhysicsSync", refresh)
        self.assertIn("if (!colliderReleaseQueued[i])", refresh)
        self.assertIn("pendingColliderRelease.Add(i);", refresh)
        self.assertIn("disabledByStreaming[i] = true;", refresh)
        self.assertIn("obstacles[i].enabled = false;", refresh)
        self.assertIn("if (forcePhysicsSync)", refresh)
        self.assertIn("FlushPendingColliderReleases();", refresh)
        self.assertIn("if (forcePhysicsSync) Physics.SyncTransforms();", refresh)
    def test_exactly_bounded_native_retirement_and_live_position(self):
        body = part("private IEnumerator ReleaseDistantAcrossFrames()", "// Parked cars can stream")
        self.assertIn("yield return null;", body)
        self.assertIn("AndroidReleaseUpdatesPerFrame = 12", CODE)
        self.assertIn("AndroidReleaseFrameBudgetSeconds = 0.001", CODE)
        self.assertIn("AndroidReleaseClockCheckInterval = 4", CODE)
        self.assertIn("colliderReleaseCursor++", body)
        self.assertIn("colliderReleaseQueued[i] = false;", body)
        self.assertIn("wanted[i] != query", body)
        self.assertIn("Vector3 framePosition = target != null ? target.position : default(Vector3);", body)
        self.assertEqual(body.count("target.position"), 2)
        self.assertIn("ColliderReleaseMargin", body)
        self.assertIn("if (dx * dx + dz * dz > retainSquared)", body)
        self.assertIn("processed >= AndroidReleaseUpdatesPerFrame", body)
        self.assertIn("Time.realtimeSinceStartupAsDouble - frameStartedAt", body)
        self.assertIn("collider.enabled = false;", body)
    def test_disable_cleans_pending_and_restores_authored_physics(self):
        off = CODE.split("private void OnDisable()", 1)[1]
        for token in ("StopCoroutine(deferredColliderRelease);",
                      "pendingColliderRelease.Clear();",
                      "colliderReleaseCursor = 0;",
                      "Array.Clear(colliderReleaseQueued, 0, colliderReleaseQueued.Length)",
                      "obstacles[i].enabled = true;"):
            self.assertIn(token, off)
    def test_dense_exit_releases_in_cap_and_no_lost_colliders(self):
        m = ReleaseModel(1500)
        m.refresh(set(range(10)))
        frame_counts = []
        while m.pending:
            frame_counts.append(m.step())
        self.assertTrue(all(1 <= n <= 12 for n in frame_counts))
        self.assertFalse(any(m.enabled[10:]))
        self.assertTrue(all(m.enabled[:10]))
        self.assertGreater(len(frame_counts), 100)
    def test_reentry_between_batches_preserves_walls(self):
        m = ReleaseModel(64)
        m.refresh({0})
        m.step()
        m.refresh({0, 48, 49})
        while m.pending: m.step()
        self.assertTrue(m.enabled[48])
        self.assertTrue(m.enabled[49])
        self.assertFalse(m.queued[48] or m.queued[49])
    def test_teleport_flushes_old_queue_immediately(self):
        m = ReleaseModel(120)
        m.refresh({1, 2, 3})
        m.step()
        m.refresh({110, 111}, teleport=True)
        self.assertFalse(m.pending)
        self.assertTrue(m.enabled[110] and m.enabled[111])
        self.assertFalse(m.enabled[1] or m.enabled[2])
    def test_queue_never_duplicates_between_refreshes(self):
        m = ReleaseModel(70)
        m.refresh({0})
        before = len(m.pending)
        m.refresh({0})
        self.assertEqual(len(m.pending), before)
        self.assertEqual(len(set(m.pending)), before)

if __name__ == "__main__":
    unittest.main(verbosity=2)
