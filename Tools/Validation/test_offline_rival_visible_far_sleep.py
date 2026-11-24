"""100 visible-far bots: native Update sleeps; rendering, hits and fights remain."""
import unittest
from pathlib import Path

source = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
director, bot = source.split("internal sealed class AarisOfflineRival", 1)

class VisibleFarCentralSleepTests(unittest.TestCase):
    def test_visible_virtual_only_not_close_physical(self):
        u = bot.split("private void Update()", 1)[1].split(
            "private void TickVirtualCombat(", 1)[0]
        eligible = u.split("bool dormantVisibleFarVirtual =", 1)[1].split(
            "SetCentralDormancy(", 1)[0]
        for token in ("!dormantOutOfVisualRange", "lastCameraRelevant",
                      "virtualBody", "playerDistanceSqr >= DetailedDistance * DetailedDistance",
                      "visualRoot == null || !visualRoot.activeSelf",
                      "nextDormantProximityProbe = nextCameraProbe;"):
            self.assertIn(token, eligible)
        self.assertIn("dormantVisibleFarVirtual);", u)
        # Changing the update dispatcher must never remove the stationary
        # player-bullet hit receiver or turn far combat into per-frame physics.
        self.assertIn("cameraRelevant && HasVisibleBody &&", u)
        self.assertIn("capsule.enabled = needsPhysicalReceiver;", u)
        self.assertIn("TickVirtualCombat(now, dt, playerPosition);", u)
        self.assertIn("bool simulationDue = !farSimulation || now >= nextFarSimulation;", u)
        self.assertLess(u.index("SetCentralDormancy("),
                        u.index("bool simulationDue ="))

    def test_100_visible_far_agents_preserve_all_combat_ticks(self):
        for fps in (15, 30, 60, 120):
            with self.subTest(fps=fps):
                phases = [i * (.35 / 100.) for i in range(100)]
                baseline_next = phases.copy()
                sleeping_next = phases.copy()
                camera_next = [0.] * 100
                proximity_next = [0.] * 100
                baseline_fights = [0] * 100
                sleeping_fights = [0] * 100
                scheduled_wakes = 0
                total_updates = fps * 10 * 100
                for frame in range(1, fps * 10 + 1):
                    now = frame / fps
                    for i in range(100):
                        if now >= baseline_next[i]:
                            baseline_fights[i] += 1
                            baseline_next[i] = now + .35
                        if now < min(sleeping_next[i], camera_next[i],
                                     proximity_next[i]):
                            continue
                        scheduled_wakes += 1
                        if now >= camera_next[i]:
                            camera_next[i] = now + .12
                        if now >= proximity_next[i]:
                            proximity_next[i] = now + .12
                        if now >= sleeping_next[i]:
                            sleeping_fights[i] += 1
                            sleeping_next[i] = now + .35
                self.assertEqual(sleeping_fights, baseline_fights)
                self.assertLess(scheduled_wakes, total_updates * .66)
                self.assertGreater(min(sleeping_fights), 20)

    def test_onscreen_far_receiver_and_mathematical_kills_still_authoritative(self):
        self.assertIn("TickRival(now, playerPosition);", bot)
        self.assertIn("now >= bot.NextDormantWakeTime", director)
        self.assertIn("bool needsPhysicalReceiver = !virtualBody ||", bot)
        self.assertIn("targetHealth.ApplyDamage(damage, gameObject);", bot)
        self.assertIn("owner.ReportRivalElimination(attacker, gameObject);", bot)
        self.assertIn("private const float FarSimulationInterval = 0.35f;", bot)

if __name__ == "__main__":
    unittest.main()
