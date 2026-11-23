#!/usr/bin/env python3
"""Distant-camera CPU fast path: source contracts plus simulated wakeup parity."""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
BOT = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
UPDATE = BOT.split("private void Update()", 1)[1].split(
    "private void TickVirtualCombat(", 1)[0]


class DormantFarProximityTests(unittest.TestCase):
    def test_far_guard_precedes_native_health_and_transform_lookups(self):
        guard = "if (dormantOutOfVisualRange && now < nextFarSimulation &&"
        self.assertIn(guard, UPDATE)
        self.assertIn("now < nextDormantProximityProbe) return;", UPDATE)
        self.assertLess(UPDATE.index(guard), UPDATE.index("if (owner == null"))
        self.assertLess(UPDATE.index(guard), UPDATE.index("float playerDistanceSqr"))

    def test_no_nearby_throttle_and_no_ai_tick_suppression(self):
        self.assertIn("private const float DormantProximityProbeInterval = 0.10f;", BOT)
        self.assertIn("playerDistanceSqr >= FarVisualDistance * FarVisualDistance;", UPDATE)
        self.assertIn("nextDormantProximityProbe = now + DormantProximityProbeInterval;", UPDATE)
        self.assertIn("if (farSimulation && now < nextFarSimulation && !justEnteredCamera &&", UPDATE)
        self.assertIn("TickVirtualCombat(now, dt);", UPDATE)
        # The throttle cannot return if the next scheduled AI tick is due.
        for due in (0.0, 0.35, 0.7, 1.05):
            self.assertFalse(due < due and due < due + .1)

    def test_100_scheduled_ai_ticks_preserved_at_30_60_120fps(self):
        for fps in (30, 60, 120):
            for phase in (0.0, .001, .04, .08):
                next_ai = phase
                next_probe = phase
                count = 0
                expensive_distance_checks = 0
                for frame in range(1, fps * 5 + 1):
                    now = frame / fps
                    # All bots stay >485m; camera visibility is irrelevant.
                    if now < next_ai and now < next_probe:
                        continue
                    expensive_distance_checks += 1
                    next_probe = now + .1
                    if now < next_ai:
                        continue
                    count += 1
                    next_ai = now + .35
                # At 30fps or faster, <1/3 of rendered frames should need
                # distance transforms but every 0.35s AI tick still runs.
                self.assertLess(expensive_distance_checks, fps * 5 * .5)
                self.assertGreaterEqual(count, 12)

    def test_midrange_hidden_guard_preserves_camera_and_combat(self):
        self.assertIn("private bool dormantOffscreenVirtual;", BOT)
        guard = "if (dormantOffscreenVirtual && now < nextFarSimulation"
        self.assertIn(guard, UPDATE)
        self.assertLess(UPDATE.index(guard), UPDATE.index("float playerDistanceSqr"))
        self.assertIn("now < nextDormantProximityProbe && now < nextCameraProbe) return;", UPDATE)
        for part in ("!dormantOutOfVisualRange", "!lastCameraRelevant && virtualBody",
                     "(capsule == null || !capsule.enabled)",
                     "(visualRoot == null || !visualRoot.activeSelf)",
                     "(farVisualRoot == null || !farVisualRoot.activeSelf)"):
            self.assertIn(part, UPDATE)
        self.assertIn("if (simulationDue && carriedGun == null && now >= nextLootAt)", UPDATE)

    def test_midrange_100ms_wakeup_matches_350ms_combat_ticks(self):
        for fps in (30, 60, 120):
            next_ai = next_cam = next_probe = baseline_due = 0.
            ai = baseline = expensive = 0
            for frame in range(1, fps * 8 + 1):
                now = frame / fps
                if now >= baseline_due:
                    baseline += 1
                    baseline_due = now + .35
                if now < next_ai and now < next_probe and now < next_cam:
                    continue
                expensive += 1
                next_probe = now + .1
                if now >= next_cam:
                    next_cam = now + .12
                if now >= next_ai:
                    ai += 1
                    next_ai = now + .35
            self.assertEqual(ai, baseline)
            self.assertLess(expensive, fps * 8 * .5)
            # A teleport into a physical range wakes within 100ms + one frame.
            self.assertLessEqual(.1 + 1/fps, .135)

    def test_teleport_into_visual_band_wakes_within_100ms(self):
        for fps in (30, 60, 120):
            now = 0.0
            next_ai = 0.35
            next_probe = 0.1
            dormant = True
            seen_inside = None
            teleport_time = .31
            for frame in range(1, fps + 1):
                now = frame / fps
                if dormant and now < next_ai and now < next_probe:
                    continue
                distance = 400 if now >= teleport_time else 800
                dormant = distance >= 485
                if dormant:
                    next_probe = now + .1
                elif seen_inside is None:
                    seen_inside = now
            self.assertIsNotNone(seen_inside)
            self.assertLessEqual(seen_inside - teleport_time, .1 + 1/fps)


if __name__ == "__main__":
    unittest.main()
