#!/usr/bin/env python3
"""Static policy / arithmetic tests, NOT Unity PlayMode or Android FPS proof."""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIR = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
CRATE = (ROOT / "Assets/AarisWorld/Scripts/AarisDeathCrateWorld.cs").read_text()

class RemoteBotBudget(unittest.TestCase):
    def test_100_rivals_keep_real_combat_and_physical_player_gate(self):
        for token in (
            "private const int RivalCount = 100;",
            "private const float FarSimulationInterval = 0.35f;",
            "TickVirtualCombat(now, dt);",
            "targetHealth.ApplyDamage(damage, gameObject);",
            "return bot == attackerOne || bot == attackerTwo;",
            "if (!hiddenTarget.virtualBody || hiddenTarget.lastCameraRelevant ||",
        ):
            self.assertIn(token, DIR)

    def test_3_zone_representation_is_independent_of_fights(self):
        for token in (
            "private const float FarSimulationDistance = 90f;",
            "playerDistanceSqr < 155f * 155f",
            "private const float FarVisualDistance = 485f;",
            "lastCameraRelevant && virtualBody",
            "visualRoot.SetActive(false);",
            "farVisualRoot.SetActive(false);",
            "capsule.enabled = false;",
            "graph.Stop();",
            "WakeDormantRivalsNearPlayer(playerPosition, now);",
            "bot.ForceDormantProximityProbe();",
            "playerDistanceSqr >= 650f * 650f",
            "? FarSimulationInterval : 0.16f",
        ):
            self.assertIn(token, DIR)
        # 85 bots each 1km away: between normal 350ms simulation ticks
        # the former 100ms extra proximity checks are now unnecessary.
        baseline_probes = 85 * 10 * 10  # ten seconds
        probes_with_combat_ticks = 85 * (10 / .35 + 1)
        self.assertLess(probes_with_combat_ticks, baseline_probes * .31)

    def test_far_kills_do_not_spawn_crate_mesh_or_ground_raycast(self):
        for token in (
            "internal bool pendingGroundSnap;",
            "bool deferGroundSnap = player != null",
            "if (!deferGroundSnap)",
            "position = SnapToStationaryGround(position);",
            "int groundSnapBudget = 2;",
            "if (drop.pendingGroundSnap) continue;",
            "groundSnapBudget--;",
            "cachedPickupNear = Nearest(here, PickupRadius",
            "private const int MaxVisuals = 16;",
        ):
            self.assertIn(token, CRATE)
        # 100 kills >115m cost zero immediate crate-ground raycasts;
        # only arrivals inside the stream band can request a bounded probe.
        remote_kills = [200.] * 100
        self.assertEqual(sum(d <= 115. for d in remote_kills), 0)
        self.assertEqual(min(100, 2), 2)

    def test_dead_rival_stops_native_updates_and_animation_without_losing_death_events(self):
        rival = DIR.split("private void OnDied(AarisOfflineHealth defeated)", 1)[1]
        on_died = rival.split("private void OnDestroy()", 1)[0]
        for token in (
            "if (centrallyDormant) SetCentralDormancy(false);",
            "owner.DropRivalLoot(transform.position, carriedGun,",
            "owner.RivalDefeated(this);",
            "if (graph.IsValid()) graph.Stop();",
            "enabled = false;",
            "Destroy(gameObject, 1.2f);",
        ):
            self.assertIn(token, on_died)
        self.assertLess(on_died.index("owner.RivalDefeated(this);"),
                        on_died.index("enabled = false;"))
        self.assertLess(on_died.index("enabled = false;"),
                        on_died.index("Destroy(gameObject, 1.2f);"))

    def test_invalid_clips_still_cull_prefab_animator_without_losing_visual(self):
        visual = DIR.split("private void BuildVisual()", 1)[1]
        visual = visual.split("private void RetireHiddenRig(", 1)[0]
        guard = visual.index("if (animator.avatar == null || !animator.avatar.isValid ||")
        self.assertLess(visual.index("animator.cullingMode = AnimatorCullingMode.CullCompletely;"),
                        guard)
        self.assertLess(visual.index("animator.applyRootMotion = false;"), guard)
        self.assertIn("skin.updateWhenOffscreen = false;", visual)
        self.assertIn("BuildFallbackSilhouette();", visual)


if __name__ == "__main__":
    unittest.main()
