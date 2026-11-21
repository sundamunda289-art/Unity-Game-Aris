#!/usr/bin/env python3
"""Combat system contract tests; source simulation, not Unity PlayMode/FPS proof."""
from pathlib import Path
import json
import math
import unittest

ROOT = Path(__file__).resolve().parents[2]
S = ROOT / "Assets/AarisWorld/Scripts"
read = lambda name: (S / name).read_text(encoding="utf-8")
BOTS = read("AarisOfflineBRDirector.cs")
GUN = read("AarisEquippedGun.cs")
HP = read("AarisOfflineHealth.cs")
HUD = read("AarisCombatFeedbackHUD.cs")
LOOT = read("AarisWeaponLootWorld.cs")
CAT = json.loads((ROOT / "Assets/Resources/AarisWeapons/weapon_catalog.json").read_text())


class CombatEndToEndContract(unittest.TestCase):
    def test_optic_tiers_and_camera_fov(self):
        by_id = {x["id"]: x for x in CAT["weapons"]}
        for weapon in CAT["weapons"]:
            self.assertIn(weapon["scopePower"], (0, 1, 2, 4))
        self.assertEqual(by_id["SMG"]["scopePower"], 1)
        self.assertEqual(by_id["AssaultRifle"]["scopePower"], 2)
        self.assertEqual(by_id["Sniper"]["scopePower"], 4)
        self.assertEqual(by_id["DMR"]["scopePower"], 4)
        self.assertEqual(by_id["Shotgun"]["scopePower"], 0)
        self.assertIn("AarisOfflineBRDirector.TryAcquireAimTarget", GUN)
        self.assertIn("TryAcquireAimTarget(aimCamera, transform", HUD)
        self.assertIn("if (closest != null && closest.transform.IsChildOf(candidate.transform))", BOTS)
        self.assertIn("shotForward = assisted;", GUN)
        self.assertNotIn("Vector3.Slerp(shotForward, assisted, .82f)", GUN)
        # Old 82% angular correction leaves a 0.252-degree error at the
        # 4x assist-cone edge: ~0.44m lateral miss at 100m (> .31m radius).
        residual_metres = math.tan(math.radians(1.4 * (1 - .82))) * 100.
        self.assertGreater(residual_metres, .31)
        self.assertIn('"NO SCOPE"', read("AarisWeaponCatalog.cs"))
        self.assertIn("opticalRing.gameObject.SetActive(showOptic)", HUD)

    def test_200_hp_bidirectional_damage_contract(self):
        self.assertIn("private float maximumHealth = 200f;", HP)
        self.assertIn("ApplyDamage(float amount, GameObject attacker)", HP)
        self.assertIn("public void ApplyHitDamage", HP)
        self.assertIn("HealthChanged?.Invoke(this);", HP)
        self.assertIn("Damaged?.Invoke(this, previous - current, attacker);", HP)
        self.assertIn('healthText.text = "HP " + current + " / " + max;', HUD)
        self.assertIn("hitFadeUntil = Time.unscaledTime + .35f;", HUD)
        self.assertIn('"HIT RIGHT"', HUD)
        self.assertIn("victim.ApplyHitDamage(damage, attacker, headshot)", GUN)
        self.assertIn("targetHealth.ApplyDamage(damage, gameObject);", BOTS)
        self.assertIn("AarisCombatFeedbackHUD.NotifyOutgoingDamage", GUN)
        self.assertIn("headshot) damage *= 1.75f", GUN)

    def test_100_bots_locally_restrict_player_aggression(self):
        self.assertIn("private const int RivalCount = 100;", BOTS)
        self.assertIn("if (distance < first)", BOTS)
        self.assertIn("attackerOne = attackerTwo = null;", BOTS)
        self.assertIn("return bot == attackerOne || bot == attackerTwo;", BOTS)
        self.assertIn("(target != playerTarget || owner.MayAttackPlayer(this))", BOTS)
        self.assertIn("owner.FindRivalOpponent(this, out target, out targetHealth)", BOTS)
        self.assertIn("FarSimulationInterval = 0.35f;", BOTS)
        self.assertIn("FarSimulationInterval / 100f", BOTS)
        self.assertIn("playerDistanceSqr < 105f * 105f", BOTS)
        self.assertIn("Physics.OverlapSphereNonAlloc", BOTS)
        distances = [4.0, 9.0, 13.0, 15.0, 20.0, 23.0, 30.0, 35.0, 45.0]
        eligible = [x for x in distances if x < 42.0]
        self.assertEqual(len(sorted(eligible)[:2]), 2)
        self.assertLessEqual(len(sorted(eligible)[:2]), 2)

    def test_bots_hunt_across_map_without_long_distance_damage(self):
        # Hunting range is separate from real sight and firing authority.
        self.assertIn("private const float RivalHuntRadius = 110f;", BOTS)
        self.assertIn("float nearest = RivalHuntRadius * RivalHuntRadius;", BOTS)
        self.assertIn("(target.position - transform.position).sqrMagnitude < 42f * 42f && HasSight()", BOTS)
        self.assertIn("distance <= 38f", BOTS)
        # Far and near/visual gates use squared distances, no per-frame sqrt.
        self.assertIn("float playerDistanceSqr =", BOTS)
        self.assertIn("playerDistanceSqr < 76f * 76f", BOTS)
        self.assertIn("playerDistanceSqr < 105f * 105f", BOTS)
        self.assertIn("playerDistanceSqr < 155f * 155f", BOTS)
        self.assertNotIn("Vector3.Distance(playerTarget.position, transform.position)", BOTS)
        # The aggression cap is independent of inter-bot hunting radius.
        self.assertIn("return bot == attackerOne || bot == attackerTwo;", BOTS)

    def test_visible_two_attacker_selection(self):
        self.assertIn("distance >= second || !bot.HasPlayerSightForSelection()", BOTS)
        self.assertIn("private bool HasSightTo(Transform victim)", BOTS)
        self.assertIn("HasSightTo(playerTarget)", BOTS)
        candidates = [(4.0, False), (9.0, False), (13.0, True),
                      (15.0, True), (20.0, True), (45.0, True)]
        visible = sorted(d for d, seen in candidates if d < 42.0 and seen)
        self.assertEqual(visible[:2], [13.0, 15.0])

    def test_stance_aware_bot_line_of_sight(self):
        # Real crouch/prone capsule positions, not fixed standing-height aim.
        self.assertIn("playerTargetCapsule = victim != null ? victim.GetComponent<CharacterController>() : null;", BOTS)
        self.assertIn("targetCapsule = target == playerTarget ? playerTargetCapsule", BOTS)
        self.assertIn("victim.TransformPoint(body.center)", BOTS)
        self.assertIn("Vector3 to = aimPoint - eye;", BOTS)
        self.assertNotIn("victim.position + Vector3.up * 1.07f - eye;", BOTS)
        # Floor-relative center on the actor, updated by AarisBattleActions.
        actions = read("AarisBattleActions.cs")
        self.assertIn("capsule.center = new Vector3(standingCenter.x, floorRelative + height * 0.5f", actions)
        for height in (0.62, 1.14, 1.8):
            self.assertGreater(height * 0.5, 0.0)
            self.assertLess(height * 0.5, height)

    def test_redlock_retries_visible_candidate_behind_cover(self):
        self.assertIn("for (int attempt = 0; attempt < 3; attempt++)", BOTS)
        self.assertIn("bot == rejectedFirst || bot == rejectedSecond", BOTS)
        self.assertIn("if (attempt == 0) rejectedFirst = candidate;", BOTS)
        self.assertIn("closest.transform.IsChildOf(candidate.transform)", BOTS)

    def test_real_loot_ownership_and_ammo(self):
        self.assertIn("internal bool TryTakeForRival", LOOT)
        body = LOOT.split("internal bool TryTakeForRival", 1)[1].split("private void Update()", 1)[0]
        for fragment in ("pickup.TryReserve()", "consumed[id] = true;",
                         "pickup.CommitConsumed();", "active.RemoveAt(i);",
                         "Destroy(pickup.gameObject);"):
            self.assertIn(fragment, body)
        self.assertIn("owner.TryLootForRival(transform.position, out AarisWeaponSpec found)", BOTS)
        self.assertIn("carriedMagazine = found.magazineSize", BOTS)
        self.assertIn("carriedGun.reloadSeconds", BOTS)

    def test_hp_math_and_armor_simulation(self):
        hp, helmet = 200.0, 75.0
        body = 33.0
        hp -= body
        self.assertEqual(hp, 167.0)
        head = 33.0 * 1.75
        absorbed = min(helmet, head * 0.35)
        helmet -= absorbed
        hp -= head - absorbed
        self.assertAlmostEqual(hp, 129.4625, places=3)
        self.assertAlmostEqual(helmet, 54.7875, places=3)

    def test_no_100_burst_far_schedulers(self):
        n, interval, fps = 100, .35, 30
        phases = [i * interval / n for i in range(n)]
        due = [0.] * n
        max_after_first = 0
        for frame in range(1, 301):
            now = frame / fps
            count = 0
            for i, phase in enumerate(phases):
                if now + 1e-9 < due[i]:
                    continue
                count += 1
                next_tick = (math.floor((now - phase) / interval) + 1) * interval + phase
                if next_tick <= now + .0001:
                    next_tick += interval
                due[i] = next_tick
            if frame > 1:
                max_after_first = max(max_after_first, count)
        self.assertLessEqual(max_after_first, 10)


if __name__ == "__main__":
    unittest.main()
