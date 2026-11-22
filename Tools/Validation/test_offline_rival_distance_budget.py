#!/usr/bin/env python3
"""Regression for 100 time-sliced distant AI agents with bounded travel drift."""
from pathlib import Path
import math
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()


def simulate(fps, seconds, distance, speed=2.85):
    now = last = next_far = pos = 0.
    frames = 0
    while now + 1e-9 < seconds:
        now += 1. / fps
        far = distance >= 90.
        if far and now < next_far:
            continue
        dt = min(max(0., now-last), .46) if far else min(1. / fps, .12)
        last = now
        if far:
            next_far = now + .35
        pos += dt * speed
        frames += 1
    return pos, frames


def simulate_hundred_phase_ticks(fps, seconds):
    """Model 100 far rivals sharing 350ms phases, rounded to phone frames."""
    phases = [index * (.35 / 100.) for index in range(100)]
    due = [0.] * 100
    updates_per_frame = []
    total_per_bot = [0] * 100
    for frame in range(1, round(fps * seconds) + 1):
        now = frame / fps
        updates = 0
        for index in range(100):
            if now + 1e-9 < due[index]:
                continue
            updates += 1
            total_per_bot[index] += 1
            next_tick = (math.floor((now - phases[index]) / .35) + 1) * .35 + phases[index]
            if next_tick <= now + .0001:
                next_tick += .35
            due[index] = next_tick
        updates_per_frame.append(updates)
    return updates_per_frame, total_per_bot


class FarRivalBudget(unittest.TestCase):
    def test_source_preserves_nearby_physics_and_sight(self):
        bot = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
        update = bot.split("private void Update()", 1)[1].split("private bool HasSight()", 1)[0]
        self.assertIn("private const float FarSimulationDistance = 90f;", bot)
        self.assertIn("private const float FarSimulationInterval = 0.35f;", bot)
        guard = update.index("if (farSimulation && now < nextFarSimulation && !justEnteredCamera) return;")
        self.assertLess(guard, update.index("owner.TryZoneRetreatDirection"))
        self.assertLess(guard, update.index("capsule.Move("))
        self.assertIn("now - lastSimulationTime", update)
        self.assertIn("Mathf.Min(Time.deltaTime, 0.12f)", update)
        self.assertIn("(target.position - transform.position).sqrMagnitude < 42f * 42f && HasSight()", update)
        self.assertIn("distance <= (carriedGun == null ? 2.25f : 38f)", update)
        self.assertIn("owner.WouldCrossInactiveCover", update)

    def test_far_movement_stays_near_realtime(self):
        for fps in (15, 30, 60, 90, 120):
            with self.subTest(fps=fps):
                far_pos, far_frames = simulate(fps, 12., 120.)
                near_pos, near_frames = simulate(fps, 12., 10.)
                self.assertAlmostEqual(far_pos, near_pos, delta=1.1)
                self.assertLess(far_frames, near_frames)
                self.assertGreater(far_frames, 20)

    def test_android_far_tick_phases_spread_cpu_and_physics(self):
        bot = SOURCE.split("internal sealed class AarisOfflineRival", 1)[1]
        self.assertIn("private const int RivalCount = 100;", SOURCE)
        self.assertIn("rival.Initialize(this, health, player, playerHealth, index);", SOURCE)
        self.assertIn("farSimulationPhase = encounterSlot * (FarSimulationInterval / 100f);", bot)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", bot)
        self.assertIn("Mathf.Floor((now - farSimulationPhase) /", bot)
        self.assertIn("if (nextTick <= now + 0.0001f)", bot)
        self.assertIn("nextFarSimulation = nextTick;", bot)
        self.assertIn("nextFarSimulation = Time.time; // First distant update remains immediate.", bot)
        self.assertIn("nextFarSimulation = now + FarSimulationInterval;", bot)
        for fps, max_per_frame in ((15, 20), (30, 10), (60, 5), (90, 4)):
            with self.subTest(fps=fps):
                collisions, totals = simulate_hundred_phase_ticks(fps, 10)
                # The first simulated frame starts all 100, whereas gameplay spawns
                # only one actor per 170ms. Ignore its synthetic start burst.
                self.assertLessEqual(max(collisions[1:]), max_per_frame)
                for ticks in totals:
                    self.assertGreaterEqual(ticks, 28)
                    self.assertLessEqual(ticks, 31)

    def test_near_bot_is_full_frame_rate(self):
        for fps in (15, 30, 60, 120):
            with self.subTest(fps=fps):
                _, ticks = simulate(fps, 3., 35.)
                self.assertEqual(ticks, round(3 * fps))


if __name__ == "__main__":
    unittest.main()
