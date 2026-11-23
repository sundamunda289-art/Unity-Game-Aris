"""Regression: centrally queued invisible bots retain exact AI tick deadlines."""
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
DIRECTOR, BOT = SOURCE.split("internal sealed class AarisOfflineRival", 1)


class CentralDormantSchedulerTests(unittest.TestCase):
    def test_native_dispatch_stops_only_for_dormant_far_or_hidden_bodies(self):
        self.assertIn("enabled = !dormant;", BOT)
        self.assertRegex(BOT, r"SetCentralDormancy\(dormantOutOfVisualRange \|\| dormantOffscreenVirtual \|\|\s*dormantVisibleFarVirtual\);")
        self.assertIn("playerDistanceSqr >= FarSimulationDistance * FarSimulationDistance", BOT)
        self.assertIn("!lastCameraRelevant && virtualBody", BOT)
        self.assertIn("owner.RegisterDormantRival(spatialSlot, this, dormant)", BOT)

    def test_deadlines_not_modified_by_central_queue(self):
        self.assertRegex(DIRECTOR, r"TickDormantRivals\((?:Time\.time|now)\);")
        self.assertIn("now >= bot.NextDormantWakeTime", DIRECTOR)
        self.assertIn("if (centrallyDormant) Update();", BOT)
        self.assertIn("Mathf.Min(nextFarSimulation, nextDormantProximityProbe)", BOT)
        self.assertIn("Mathf.Min(nextDormantProximityProbe, nextCameraProbe)", BOT)
        self.assertIn("private const float FarSimulationInterval = 0.35f;", BOT)
        self.assertIn("private const float DormantProximityProbeInterval = 0.10f;", BOT)

    def test_100_far_bot_combat_ticks_identical_to_old_per_frame_driver(self):
        for fps in (30, 60, 120):
            original = [i * .0035 for i in range(100)]
            central = original.copy()
            proximity = [0. for _ in range(100)]
            old_ticks = [0] * 100
            new_ticks = [0] * 100
            for frame in range(1, 10 * fps + 1):
                now = frame / fps
                for i in range(100):
                    if now >= original[i]:
                        old_ticks[i] += 1
                        original[i] = now + .35
                    # Central queue calls the old Update at whichever of the
                    # existing 100ms proximity or 350ms combat deadlines is due.
                    if now < central[i] and now < proximity[i]:
                        continue
                    if now >= proximity[i]:
                        proximity[i] = now + .1
                    if now >= central[i]:
                        new_ticks[i] += 1
                        central[i] = now + .35
            self.assertEqual(new_ticks, old_ticks)

    def test_global_wake_deadline_preserves_existing_combat_ticks(self):
        # The global min only avoids an idle roster scan. It must not modify
        # the per-bot simulation/proximity clocks or suppress remote kills.
        self.assertIn("private float nextDormantSweepAt = float.PositiveInfinity;", DIRECTOR)
        self.assertIn("now < nextDormantSweepAt", DIRECTOR)
        self.assertIn("nextDormantSweepAt = Mathf.Min(nextDormantSweepAt,", DIRECTOR)
        self.assertIn("if (now >= bot.NextDormantWakeTime)", DIRECTOR)
        for fps in (30, 60, 120):
            for count in (1, 10, 40, 100):
                baseline = [i * .0035 for i in range(count)]
                optimized = baseline.copy()
                baseline_probe = [0.] * count
                optimized_probe = baseline_probe.copy()
                baseline_ticks = [0] * count
                optimized_ticks = [0] * count
                next_global_wake = 0.
                scans = 0
                for frame in range(1, fps * 10 + 1):
                    now = frame / fps
                    for i in range(count):
                        if now >= baseline_probe[i]:
                            baseline_probe[i] = now + .1
                        if now >= baseline[i]:
                            baseline_ticks[i] += 1
                            baseline[i] = now + .35
                    if now < next_global_wake:
                        continue
                    scans += 1
                    next_global_wake = float("inf")
                    for i in range(count):
                        if now >= optimized_probe[i]:
                            optimized_probe[i] = now + .1
                        if now >= optimized[i]:
                            optimized_ticks[i] += 1
                            optimized[i] = now + .35
                        next_global_wake = min(next_global_wake,
                                               optimized_probe[i], optimized[i])
                self.assertEqual(baseline_ticks, optimized_ticks)
                self.assertLessEqual(scans, fps * 10)
                if count == 1:
                    self.assertLess(scans, fps * 10 // 2)

    def test_benchmark_drives_queue_when_director_disabled(self):
        bench = (Path(__file__).resolve().parents[2] /
                 "Assets/AarisWorld/Editor/AarisBotFrameBenchmark.cs").read_text()
        self.assertIn("director.TickDormantRivals(Time.time);", bench)

    def test_real_scene_soak_drives_queue_when_director_disabled(self):
        qa = (Path(__file__).resolve().parents[2] /
              "Assets/AarisWorld/Editor/AarisFullSceneCombatSoak.cs").read_text()
        self.assertIn("director.enabled = false;", qa)
        self.assertIn("if (stage >= 3 && director != null && !director.enabled)", qa)
        self.assertIn("director.TickDormantRivals(Time.time);", qa)


if __name__ == "__main__":
    unittest.main()
