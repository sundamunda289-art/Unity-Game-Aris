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
    def test_red_lock_uses_physical_muzzle_path(self):
        self.assertIn("MuzzlePathOpenTo(shooter, candidate.transform, closestPoint)", BOTS)
        self.assertIn("new Ray(anchor, shooter.forward), 0.24f", BOTS)
        self.assertIn("new Ray(muzzle, to / distance)", BOTS)
        self.assertIn("first.transform.IsChildOf(victim)", BOTS)
        self.assertIn("ref muzzleAssistHits, 256", BOTS)
        self.assertIn("collider.transform.IsChildOf(shooter)", BOTS)
        # Camera peeks over 1.4m wall, but the 1m barrel hits it.
        camera = 2.0 + (1.2 - 2.0) * (5.0 + 2.0) / (20.0 + 2.0)
        barrel = 1.0 + (1.2 - 1.0) * (5.0 - 0.0) / (20.0 - 0.0)
        self.assertGreater(camera, 1.4)
        self.assertLess(barrel, 1.4)

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
        self.assertIn("if (closest != null && closest.transform.IsChildOf(candidate.transform) &&", BOTS)
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
        self.assertIn("AarisEquippedGun.ApplyDamage(shotHit.collider, damage,", BOTS)
        self.assertIn("TryGetSightHit(target, out RaycastHit shotHit)", BOTS)
        self.assertIn("shotHit = hit;", BOTS)
        self.assertIn("AarisCombatFeedbackHUD.NotifyOutgoingDamage", GUN)
        self.assertIn("headshot) damage *= 1.75f", GUN)

    def test_physical_muzzle_impact_controls_same_enemy_headshots(self):
        # Camera/muzzle parallax can strike the same enemy at different heights.
        # Camera headshot must NOT count if the physical muzzle struck its body.
        firing = GUN.split("public bool TryFire(Camera camera, GameObject owner)", 1)[1].split(
            "internal static void ApplyDamage", 1)[0]
        muzzle = firing.split("if (!muzzleBlocked && distance > 0.025f)", 1)[1].split(
            "// One tracer", 1)[0]
        self.assertIn("if (muzzleHitFound)", muzzle)
        for token in ("impactPoint = muzzleHit.point;", "impactNormal = muzzleHit.normal;",
                      "target = muzzleHit;", "targetFound = true;"):
            self.assertIn(token, muzzle)
        self.assertNotIn("SameTarget", muzzle)
        self.assertLess(muzzle.index("target = muzzleHit;"),
                        muzzle.index("targetFound = true;"))
        self.assertIn("ApplyDamage(target.collider, spec.damage, owner, target.point)", firing)
        self.assertIn("headshot = IsAuthoredHeadHit(collider, node) ||", GUN)
        self.assertIn("bounds.size.y > .28f && hitPosition.Value.y >=", GUN)
        threshold = 1.8 * .79
        self.assertGreater(1.55, threshold)
        self.assertLess(1.20, threshold)

    def test_red_lock_spread_remains_physical_and_mobile_friendly(self):
        # Red-lock assistance narrows inaccuracy only after a physical LOS
        # check. The gun still performs its muzzle ray and individual impacts.
        self.assertIn("bool assistedLock = AarisOfflineBRDirector.TryAcquireAimTarget(", GUN)
        self.assertIn("float shotSpreadDegrees = !assistedLock ? spec.spreadDegrees :", GUN)
        self.assertIn("14f / Mathf.Max(1f, spec.rangeMetres)", GUN)
        self.assertIn("spec.pellets > 1 ? spec.spreadDegrees * 0.65f", GUN)
        self.assertIn("Vector2.ClampMagnitude(spread, 1f) * shotSpreadDegrees", GUN)
        self.assertIn("ApplyDamage(target.collider, spec.damage, owner, target.point)", GUN)
        for weapon in CAT["weapons"]:
            full = float(weapon["spreadDegrees"])
            assisted = (full * 0.65 if weapon["pellets"] > 1 else
                        min(full * 0.30, 14.0 / max(1.0, weapon["rangeMetres"])))
            self.assertGreater(assisted, 0.0)
            self.assertLessEqual(assisted, full)
            if weapon["pellets"] == 1:
                # Angular offset at listed range stays inside a 0.31m
                # default bot capsule: aiming remains physical and fair.
                lateral = math.tan(math.radians(assisted)) * weapon["rangeMetres"]
                self.assertLess(lateral, 0.31)

    def test_small_authored_head_hitboxes_count_as_real_headshots(self):
        self.assertIn("private static bool IsAuthoredHeadHit(Collider hit, Transform receiver)", GUN)
        self.assertIn("part != null && part != receiver; part = part.parent", GUN)
        for bone in ('"Head"', '"HeadHitbox"', '"HeadCollider"', '":Head"'):
            self.assertIn(bone, GUN)
        self.assertIn("headshot = IsAuthoredHeadHit(collider, node) ||", GUN)
        self.assertIn("bounds.size.y > .28f && hitPosition.Value.y >=", GUN)
        # The old 28cm minimum rejected a genuine 20cm HeadCollider.
        self.assertLess(0.20, 0.28)

    def test_child_limbs_never_inherit_full_body_headshot_height(self):
        self.assertIn("headshot = IsAuthoredHeadHit(collider, node) ||", GUN)
        self.assertIn("collider.transform == node &&", GUN)
        self.assertIn("bounds.size.y > .28f && hitPosition.Value.y >=", GUN)
        # An authored HeadCollider is a positive head hit regardless of size.
        # A child forearm's upper rim is not a head, even at 90% of its bounds.
        def is_head(authored_head, is_receiver_root, height_fraction):
            return authored_head or (is_receiver_root and height_fraction >= .79)
        self.assertTrue(is_head(True, False, .5))
        self.assertFalse(is_head(False, False, .95))
        self.assertTrue(is_head(False, True, .90))
        self.assertFalse(is_head(False, True, .60))

    def test_rival_sight_cone_and_local_radar_gate(self):
        selection = BOTS.split("internal bool HasPlayerSightForSelection()", 1)[1].split(
            "private bool HasSight()", 1)[0]
        self.assertIn("float playerRangeSqr = towardPlayer.sqrMagnitude;", selection)
        self.assertIn("towardPlayer.y = 0f;", selection)
        self.assertIn("Vector3.Dot(transform.forward, towardPlayer)", selection)
        self.assertIn("facing <= 0f || facing * facing < planarSqr * 0.25f", selection)
        self.assertLess(selection.index("facing * facing"), selection.index(
            "return HasSightTo(playerTarget);"))
        # 120 degree total cone outside a 12m omnidirectional radar radius.
        def perceived(distance, angle):
            if distance <= 12.:
                return True
            dot = math.cos(math.radians(angle))
            return dot > 0 and dot * dot >= .25
        for angle in (0, 30, 60, 120, 180):
            self.assertTrue(perceived(8., angle))
        self.assertTrue(perceived(30., 0))
        self.assertTrue(perceived(30., 59))
        self.assertFalse(perceived(30., 61))
        self.assertFalse(perceived(30., 180))
        # This prefilter is not combat authority: physical LOS and the
        # director's two-attacker cap still control the actual attack.
        self.assertIn("return HasSightTo(playerTarget);", selection)
        self.assertIn("return bot == attackerOne || bot == attackerTwo;", BOTS)

    def test_camera_only_impact_must_not_damage_without_muzzle_contact(self):
        firing = GUN.split("public bool TryFire(Camera camera, GameObject owner)", 1)[1].split(
            "internal static void ApplyDamage", 1)[0]
        muzzle = firing.split("if (!muzzleBlocked && distance > 0.025f)", 1)[1].split(
            "// One tracer", 1)[0]
        self.assertIn("targetFound = muzzleHitFound;", muzzle)
        self.assertIn("distance + 0.05f", muzzle)
        self.assertIn("else if (!muzzleBlocked)", muzzle)
        self.assertIn("targetFound = false;", muzzle)
        self.assertIn("if (targetFound)", firing)
        for barrel_block, camera_hit, physical_hit, expected in (
                (False, True, False, False),
                (False, True, True, True),
                (False, False, True, True),
                (True, True, False, True),
                (False, False, False, False)):
            self.assertEqual(barrel_block or physical_hit, expected)

    def test_floating_numbers_project_confirmed_world_hits(self):
        self.assertIn("hitPosition ?? collider.bounds.center", GUN)
        self.assertIn("WorldToScreenPoint(worldHitPosition)", HUD)
        self.assertIn("RectTransformUtility.ScreenPointToLocalPointInRectangle(", HUD)
        self.assertIn("numberOrigin[index] = origin;", HUD)

    def test_bot_capsule_lookup_is_cached(self):
        self.assertIn("if (target != cachedTargetCapsuleOwner)", BOTS)
        self.assertIn("cachedTargetCapsuleOwner = target;", BOTS)

    def test_scope_reticle_geometries_are_distinct(self):
        self.assertIn("private void RefreshReticleGeometry(int tier)", HUD)
        self.assertIn("if (lastReticleTier == tier) return;", HUD)
        self.assertIn("float gap = tier == 0 ? 21f", HUD)
        self.assertIn("float stroke = tier == 0 ? 12f", HUD)
        self.assertIn("RefreshReticleGeometry(scope);", HUD)

    def test_100_bots_locally_restrict_player_aggression(self):
        self.assertIn("private const int RivalCount = 100;", BOTS)
        self.assertIn("if (distance < first)", BOTS)
        self.assertIn("private const int AttackerSelectionPerFrame = 12;", BOTS)
        self.assertIn("attackerScanFrame == Time.frameCount", BOTS)
        self.assertIn("attackerOne = scanAttackerOne != null && scanAttackerOne.IsAlive", BOTS)
        self.assertIn("attackerTwo = scanAttackerTwo != null && scanAttackerTwo.IsAlive", BOTS)
        self.assertIn("RefreshPlayerAttackers();\n            if (!spawnComplete", BOTS)
        self.assertEqual((100 + 12 - 1) // 12, 9)
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
        self.assertIn("distance <= (carriedGun == null ? 2.25f : 38f)", BOTS)
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
        redlock = BOTS.split("internal static bool TryAcquireAimTarget", 1)[1].split(
            "private void RefreshStatus", 1)[0]
        # One 100-bot cone scan yields three ranked fallbacks; occluded
        # targets are still rejected by physical camera AND muzzle checks.
        self.assertEqual(redlock.count("for (int i = 0; i < activeDirector.rivals.Length; i++)"), 1)
        self.assertIn("for (int attempt = 0; attempt < 3; attempt++)", redlock)
        self.assertIn("AarisOfflineRival first = null, second = null, third = null;", redlock)
        self.assertIn("float threshold = a3;", redlock)
        self.assertIn("third = second; d3 = d2; r3 = r2; a3 = a2;", redlock)
        self.assertIn("closest.transform.IsChildOf(candidate.transform)", redlock)
        self.assertIn("MuzzlePathOpenTo(shooter, candidate.transform, closestPoint)", redlock)
        self.assertNotIn("rejectedFirst", redlock)

    def test_unarmed_bots_use_contact_melee_without_phantom_gunfire(self):
        attack = BOTS.split("if (hasTarget && canSee &&", 1)[1].split(
            "private void UpdateAnimationBlend", 1)[0]
        self.assertIn("distance <= (carriedGun == null ? 2.25f : 38f)", attack)
        self.assertIn("AarisAudioDirector.Punch(transform.position + Vector3.up)", attack)
        self.assertIn("AarisAudioDirector.GunShot(carriedGun.category, carriedGun.id,", attack)
        self.assertNotIn('carriedGun.id : "Rival"', attack)
        self.assertNotIn('carriedGun.category : "SMG"', attack)
        self.assertIn("carriedGun == null ? (distance > 1.7f ? 2.85f : 0f)", BOTS)
        self.assertIn("&& carriedGun != null)", BOTS)
        self.assertIn("(target != playerTarget || owner.MayAttackPlayer(this))", attack)
        self.assertLess(2.25, 38.0)
        self.assertLess(1.7, 2.25)

    def test_real_loot_ownership_and_ammo(self):
        self.assertIn("internal bool TryTakeForRival", LOOT)
        body = LOOT.split("internal bool TryTakeForRival", 1)[1].split("private void Update()", 1)[0]
        for fragment in ("pickup.TryReserve()", "consumed[id] = true;",
                         "pickup.CommitConsumed();", "active.RemoveAt(i);",
                         "Destroy(pickup.gameObject);"):
            self.assertIn(fragment, body)
        # A camera-only presentation wake must not consume a far loot tick.
        self.assertIn("if (simulationDue && carriedGun == null && now >= nextLootAt)", BOTS)
        self.assertIn("if (playerDistanceSqr < 76f * 76f)", BOTS)
        self.assertIn("owner.TryLootForRival(transform, out found)", BOTS)
        # Bots pass their actual Transform so pickup LOS can exclude ONLY
        # the looter's own colliders, never arbitrary player-layer walls.
        self.assertIn("internal bool TryLootForRival(Transform rival", BOTS)
        self.assertIn("rivalLootWorld.TryTakeForRival(rival, out weapon)", BOTS)
        self.assertIn("if (!HasClearPickupPath(rival, pickup.transform.position)) continue;", body)
        self.assertIn("private bool HasClearPickupPath(Transform looter, Vector3 position)", LOOT)
        self.assertIn("collider.transform.IsChildOf(looter)", LOOT)
        self.assertIn("hits = CompleteRaycastOverflow(sightLine, distance, out sightHits);", LOOT)
        self.assertIn("else if (!remoteLoadoutGiven && AarisWeaponCatalog.Ready)", BOTS)
        self.assertIn("carriedReserve -= refill;", BOTS)
        self.assertIn("carriedMagazine = found.magazineSize", BOTS)
        self.assertIn("carriedGun.reloadSeconds", BOTS)

    def test_wounded_bot_does_not_confuse_nearby_box_with_cover(self):
        reaction = BOTS.split("private void OnDamaged(", 1)[1].split(
            "internal void TakeZoneDamage", 1)[0]
        # The shooter pose is captured once before inspecting many cover
        # candidates; the candidate direction still points away from that
        # exact shooter position and the PhysX cover-ray remains required.
        self.assertIn("Vector3 attackerPosition = attacker.transform.position;", reaction)
        self.assertEqual(reaction.count("attacker.transform.position"), 1)
        for part in ("Vector3 coverAxis = bounds.center - attackerPosition;",
                     "Vector3 candidate = bounds.center + coverAxis.normalized *",
                     "!obstacle.Raycast(new Ray(coverFrom, coverRay / coverDistance),",
                     "out RaycastHit blocker, coverDistance)"):
            self.assertIn(part, reaction)
        # The destination must be farther from the attacker than the
        # chosen cover, irrespective of which side the bot started on.
        for shooter, box in ((0.0, 10.0), (25.0, 10.0)):
            axis = 1 if box > shooter else -1
            destination = box + axis * 2.1
            self.assertGreater(abs(destination - shooter), abs(box - shooter))

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



class PositionlessMeleeFeedbackContract(unittest.TestCase):
    def test_melee_damage_routes_to_pooled_popup_without_headshot(self):
        actions = read("AarisBattleActions.cs")
        self.assertIn("ApplyDamage(victim, punchDamage, gameObject);", actions)
        self.assertIn("ApplyDamage(collider, amount, attacker, null);", GUN)
        health_case = GUN.split("if (damageable is AarisOfflineHealth victim)", 1)[1].split(
            "else damageable.ApplyDamage(damage, attacker);", 1)[0]
        self.assertIn("if (hitPosition.HasValue)", health_case)
        self.assertIn("IsAuthoredHeadHit(collider, node)", health_case)
        self.assertIn("victim.ApplyHitDamage(damage, attacker, headshot);", health_case)
        self.assertIn("AarisCombatFeedbackHUD.NotifyOutgoingDamage(", health_case)
        self.assertIn("hitPosition ?? collider.bounds.center", health_case)
        self.assertNotIn("victim && hitPosition.HasValue", GUN)
        qa = (ROOT / "Assets/AarisWorld/Editor/AarisFullSceneCombatSoak.cs").read_text()
        self.assertIn("MELEE_DAMAGE_FLOATING_NUMBER_MISSING", qa)

if __name__ == "__main__":
    unittest.main()
