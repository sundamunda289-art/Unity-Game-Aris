#!/usr/bin/env python3
"""Gameplay-source contracts for pro gunplay and bot death loot; not native Play Mode."""
from pathlib import Path
import json
import unittest

ROOT = Path(__file__).resolve().parents[2]
DIR = ROOT / "Assets/AarisWorld/Scripts"
def read(name): return (DIR / name).read_text("utf-8")

GUN = read("AarisEquippedGun.cs")
RIVAL = read("AarisOfflineBRDirector.cs")
CRATE = read("AarisDeathCrateWorld.cs")
FX = read("AarisRivalGunfireVfx.cs")
AUDIO = read("AarisAudioDirector.cs")
INVENTORY = read("AarisWeaponInventory.cs")
FLOOR = read("AarisWeaponLootWorld.cs")
MEDICAL = read("AarisOfflineTacticalHUD.cs")
HUD = read("AarisWeaponHUD.cs")
SPEC = read("AarisWeaponCatalog.cs")
CAT = json.loads((ROOT / "Assets/Resources/AarisWeapons/weapon_catalog.json").read_text("utf-8"))

class ProGunplayAndDeathCrate(unittest.TestCase):
    def test_all_seven_authored_damage_profiles_are_distinct(self):
        guns = {w["id"]: w for w in CAT["weapons"]}
        self.assertEqual(set(guns), {"SMG","AssaultRifle","BattleRifle","Sniper","Shotgun","DMR","Pistol"})
        self.assertEqual(len(set(x["damage"] for x in guns.values())), 7)
        self.assertEqual(len(set(x["rpm"] for x in guns.values())), 7)
        self.assertIn("headshotMultiplier >= 1f && headshotMultiplier <= 4f", SPEC)
        self.assertEqual(guns["Sniper"]["headshotMultiplier"], 2.25)
        self.assertLess(guns["SMG"]["headshotMultiplier"],
                        guns["DMR"]["headshotMultiplier"])
        self.assertIn("ApplyDamage(target.collider, spec.damage, owner, target.point,", GUN)
        self.assertIn("spec.headshotMultiplier);", GUN)
        self.assertIn("victim.ApplyHitDamage(damage, attacker, headshot)", GUN)

    def test_authoritative_firing_gate_and_pooled_player_effects(self):
        firing = GUN.split("public bool TryFire(Camera camera, GameObject owner)",1)[1].split(
            "private static bool IsAuthoredHeadHit",1)[0]
        self.assertLess(firing.index("magazine--;"), firing.index("TriggerMuzzleFlash();"))
        self.assertIn("ScheduleCasingEjection();",firing)
        self.assertIn("TriggerTracer(muzzle, impactPoint);",firing)
        self.assertIn("targetFound = muzzleHitFound;",firing)
        self.assertIn("muzzleLight.shadows = LightShadows.None;",GUN)
        self.assertIn("if (muzzleLight != null) muzzleLight.enabled = false;",GUN)
        self.assertIn("groundY = landing.point.y + 0.012f;",GUN)
        self.assertIn("casingVelocity.y = -casingVelocity.y * 0.28f;",GUN)
        self.assertNotIn("new LineRenderer()",firing)
        self.assertNotIn("GameObject.CreatePrimitive",firing)

    def test_shell_ground_ignores_other_living_bots_and_physics_vehicles(self):
        casing = GUN.split("internal static bool TryFindCasingGround(", 1)[1].split(
            "private static Mesh MuzzleFlameMesh()", 1)[0]
        for required in (
            "support.attachedRigidbody != null",
            "support.GetComponentInParent<CharacterController>() != null",
            "support.transform.IsChildOf(shooter)",
            "hit.normal.y <= .45f",
            "hit.distance >= nearest",
        ):
            self.assertIn(required, casing)
        self.assertEqual(casing.count("AarisPhysicsHitBuffer.Raycast("), 1)
        self.assertNotIn("new GameObject(", casing)
        self.assertNotIn("Physics.Raycast(", casing)
        self.assertIn("AarisEquippedGun.TryFindCasingGround(port, shooter,", FX)
        native = (ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text("utf-8")
        self.assertIn("Shell falls past nearby live rival capsule to stationary floor", native)
        self.assertIn("Shell falls past kinematic vehicle to stationary floor", native)
        self.assertIn("rivalCapsule.enabled = false;", native)
        self.assertIn("vehicleCollider.enabled = false;", native)

    def test_every_automatic_round_has_a_separate_reusable_streak(self):
        self.assertIn("private const int ExtraTracerSlots = 2;", GUN)
        self.assertIn("new TracerState[ExtraTracerSlots]", GUN)
        self.assertIn("tracerCursor++ % (ExtraTracerSlots + 1)", GUN)
        self.assertIn("tracerTrail[index - 1] = shot;", GUN)
        fire = GUN.split("public bool TryFire(", 1)[1].split(
            "private static bool IsAuthoredHeadHit", 1)[0]
        self.assertIn("if (pellet == 0 && !muzzleBlocked)", fire)
        self.assertIn("TriggerTracer(muzzle, impactPoint);", fire)
        self.assertNotIn("++tracerShotCounter % 3", fire)
        # Three slots outlive even the capped sniper-range streak lifetime.
        min_interval = 60.0 / max(w["rpm"] for w in CAT["weapons"])
        longest_tracer = 0.21
        self.assertGreater(3 * min_interval, longest_tracer)
        update = GUN.split("private void TickTracer()", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("for (int i = 0; i < tracerTrail.Length; i++)", update)
        self.assertIn("shot.renderer.SetPosition(1, shot.start + segment * head)", update)
        self.assertNotIn("new GameObject(", update)

    def test_full_range_streaks_and_cosmetic_rng_isolation(self):
        flash = GUN.split("private void TriggerMuzzleFlash()", 1)[1].split(
            "private static Material CasingMaterial", 1)[0]
        tracer = GUN.split("private void TriggerTracer(", 1)[1].split(
            "private void TickTracer()", 1)[0]
        bot = FX.split("private void EmitWithBarrel(", 1)[1].split(
            "internal void EmitForGun(", 1)[0]
        self.assertIn("float distance = length;", tracer)
        self.assertIn("Vector3 end = impact;", tracer)
        self.assertIn("Mathf.Clamp(Mathf.Max(distance / 1250f,", tracer)
        self.assertIn("frameFloor), 0.055f, 0.21f)", tracer)
        self.assertNotIn("Mathf.Min(length, 64f)", tracer)
        self.assertIn("slot.end = target;", bot)
        self.assertIn("Mathf.Clamp(Mathf.Max(length / 1250f, frameFloor), .055f, .21f)", bot)
        self.assertNotIn("Mathf.Min(length, 50f)", bot)
        self.assertIn("muzzleVisualSeed = state.SpreadSeed ^ 0xF1A5C1u;", GUN)
        self.assertIn("NextMuzzleVisualRange(0.80f, 1.22f)", flash)
        self.assertIn("NextMuzzleVisualRange(0f, 360f)", flash)
        self.assertNotIn("UnityEngine.Random", flash)
        self.assertIn("Muzzle flame variation never changes global combat RNG",
                      (ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text("utf-8"))
        self.assertIn("Long-range hit tracer reaches real impact with bounded lifetime",
                      (ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text("utf-8"))

    def test_rival_scavenging_preserves_floor_rounds_for_death_crate(self):
        self.assertIn("out AarisWeaponState acquired", FLOOR)
        self.assertIn("acquired = pickup.State;", FLOOR)
        self.assertNotIn("acquired = pickup.Spec;", FLOOR)
        self.assertIn("out AarisWeaponState weapon", RIVAL)
        self.assertIn("owner.TryLootForRival(transform, out scavenged);", RIVAL)
        self.assertIn("carriedGun = scavenged.Spec;", RIVAL)
        self.assertIn("carriedMagazine = Mathf.Clamp(scavenged.Magazine, 0,", RIVAL)
        self.assertIn("carriedReserve = Mathf.Max(0, scavenged.Reserve);", RIVAL)
        self.assertIn("carriedMagazine = found.magazineSize;", RIVAL)
        self.assertIn("owner.DropRivalLoot(transform.position, carriedGun,", RIVAL)

    def test_weapon_class_tracer_and_shadowless_light_profiles(self):
        # Tuned at equip; all three player streak renderers share the profile.
        profile = GUN.split("private void ApplyTracerWeaponProfile()", 1)[1].split(
            "private void TriggerTracer(", 1)[0]
        self.assertIn("ApplyTracerWeaponProfile();", GUN)
        self.assertIn("tracerRenderer.widthMultiplier = width;", profile)
        self.assertIn("tracerTrail[i].renderer.widthMultiplier = width;", profile)
        self.assertIn("muzzleLight.intensity = intensity;", profile)
        self.assertIn("muzzleLight.range =", profile)
        for category in ("Pistol", "SMG", "Sniper", "Shotgun", "DMR", "BattleRifle"):
            self.assertIn('"' + category + '"', profile)
        self.assertNotIn("new GameObject(", profile)
        # The shared bot slot resets calibre after reuse by another gun.
        bot = FX.split("internal void EmitForGun(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("flash.tracer.widthMultiplier =", bot)
        self.assertIn('gun.id == "BattleRifle"', bot)
        self.assertNotIn("new GameObject(", bot)

    def test_streamed_crate_beam_pulses_without_frame_allocations(self):
        scan = CRATE.split("private void Update()", 1)[1].split(
            "private void Release(", 1)[0]
        self.assertIn("drop.beamPose.localScale", scan)
        self.assertIn("Mathf.Sin(Time.time * 2.8f)", scan)
        self.assertIn('view.transform.Find("Vertical loot marker")', scan)
        self.assertIn("chosen.HasContent", scan)
        self.assertIn("drop.beamPose = null;", CRATE)
        self.assertNotIn("new Material(", scan)
        self.assertNotIn("new GameObject(", scan)

    def test_shell_ejection_deadlines_are_lossless_under_slow_frames(self):
        firing = GUN.split("public bool TryFire(",1)[1].split(
            "private static bool IsAuthoredHeadHit",1)[0]
        self.assertIn("ScheduleCasingEjection();", firing)
        self.assertIn("private const int MaximumPendingCasingEjections = 16;", GUN)
        self.assertIn("new float[MaximumPendingCasingEjections]", GUN)
        self.assertIn("pendingCasingEjections++;", GUN)
        self.assertIn("casingDeadlines[tail] = Time.time + delay;", GUN)
        self.assertIn("private void TickCasingEjections()", GUN)
        self.assertIn("Time.time >= casingDeadlines[casingDeadlineHead]", GUN)
        self.assertIn("EjectCasingNow();", GUN)
        self.assertIn("TickCasingEjections();", GUN.split("private void Update()",1)[1])
        self.assertIn("pendingCasingEjections = 0;", GUN)
        self.assertNotIn("casingEjectAt", GUN)
        self.assertIn("float dt = Mathf.Min(Time.deltaTime, 0.12f);", GUN)
        self.assertIn("float dt = Mathf.Min(Time.deltaTime, .12f);", FX)
        self.assertNotIn("float dt = Mathf.Min(Time.deltaTime, .05f);", FX)
        # At a 10Hz frame time the old 50ms cap halved falling speed.
        for fps in (10, 12, 15, 30, 60, 90):
            delta = 1.0 / fps
            self.assertAlmostEqual(min(delta, .12), delta)
        delayed = GUN.split("private void ScheduleCasingEjection()",1)[1].split(
            "private void EjectCasingNow()",1)[0]
        self.assertNotIn("new GameObject(", delayed)
        self.assertNotIn("GameObject.CreatePrimitive", delayed)

    def test_automatic_shells_survive_until_ground_bounce(self):
        import math
        self.assertIn("private const int ExtraShellSlots = 9;", GUN)
        self.assertIn("shellCursor++ % (ExtraShellSlots + 1)", GUN)
        self.assertIn("shellTrail[i] = new ShellState", GUN)
        self.assertIn("for (int i = 0; i < shellTrail.Length; i++)", GUN)
        self.assertIn("slot.velocity += Physics.gravity * dt;", GUN)
        self.assertIn("if (slot.velocity.y < 0.5f) slot.rested = true;", GUN)
        eject = GUN.split("private void EjectCasingNow()",1)[1].split(
            "private void HideCasing()",1)[0]
        self.assertNotIn("GameObject.CreatePrimitive",eject)
        self.assertNotIn("new GameObject(",eject)
        # At the fastest authored rate ten slots live for more than
        # one ballistic fall from the muzzle to foot level.
        fastest_rpm = max(gun["rpm"] for gun in CAT["weapons"])
        cycle = (9 + 1) * 60.0 / fastest_rpm
        fall = (1.65 + math.sqrt(1.65**2 + 2 * 9.81 * 1.3)) / 9.81
        self.assertGreater(cycle,fall)

    def test_player_and_bot_casings_share_capped_geometry_without_native_colliders(self):
        self.assertIn("internal static Mesh SharedCasingMesh()", GUN)
        self.assertIn("sharedCasingMesh = mesh;", GUN)
        self.assertIn("mesh.UploadMeshData(true);", GUN)
        self.assertIn("triangles = new int[sides * 12]", GUN)
        self.assertIn("typeof(MeshFilter), typeof(MeshRenderer)", GUN)
        self.assertIn("AarisEquippedGun.SharedCasingMesh()", FX)
        self.assertNotIn("GameObject.CreatePrimitive(PrimitiveType.Cylinder)", GUN)
        self.assertNotIn("GameObject.CreatePrimitive(PrimitiveType.Cylinder)", FX)
        self.assertNotIn("Destroy(shellCollider)", GUN)
        self.assertNotIn("Destroy(collider)", GUN)
        self.assertIn("Player casing pool reuses one sealed mesh", (
            ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text())
        self.assertIn("Six nearby bot ejections create no transient native Colliders", (
            ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text())

    def test_nearby_rival_only_pooled_visuals(self):
        self.assertIn("private const int MaximumSlots = 6;",FX)
        self.assertIn("if (observer == null || shooter == null || Time.timeScale <= 0f ||",FX)
        self.assertIn("70f * 70f",FX)
        self.assertIn("slots[index] = slot = CreateSlot();",FX)
        self.assertIn("slot.tracer.widthMultiplier = .008f;",FX)
        self.assertIn("casing.pose.Rotate(casing.spin * dt, Space.World);",FX)
        self.assertIn("owner.ShowRivalGunfire(transform, shotHit.point, carriedGun);",RIVAL)
        self.assertIn("shotHit.point, carriedGun.headshotMultiplier);",RIVAL)
        self.assertIn("AarisEquippedGun.SharedMuzzleFlameMesh()",FX)
        self.assertNotIn("ShowRivalGunfire",RIVAL.split("private void TickVirtualCombat",1)[1])

    def test_rival_casing_survives_tracer_slot_reuse(self):
        self.assertIn("private const int MaximumSlots = 6;", FX)
        self.assertIn("private const int MaximumCasingSlots = 12;", FX)
        self.assertIn("new CasingSlot[MaximumCasingSlots]", FX)
        emit = FX.split("internal void Emit(Transform shooter", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("nextSlot++ % MaximumSlots", emit)
        self.assertIn("nextCasingSlot++ % MaximumCasingSlots", emit)
        self.assertIn("casings[casingIndex] = casing = CreateCasingSlot();", emit)
        self.assertIn("casing.renderer.enabled = true;", emit)
        self.assertIn("casing.until = now + .85f;", emit)
        self.assertNotIn("slot.casing", emit)
        self.assertNotIn("GameObject.CreatePrimitive", emit)
        self.assertNotIn("new GameObject(", emit)
        updates = FX.split("private void Update()", 1)[1]
        self.assertIn("for (int i = 0; i < slots.Length; i++)", updates)
        self.assertIn("for (int i = 0; i < casings.Length; i++)", updates)
        self.assertIn("if (casing == null || !casing.renderer.enabled) continue;", updates)
        self.assertIn("casing.velocity += Physics.gravity * dt;", updates)
        self.assertIn("if (casing.velocity.y < .5f) casing.grounded = true;", updates)
        self.assertNotIn("Physics.Raycast", updates)
        self.assertNotIn("Destroy(hitbox);", FX)
        self.assertNotIn("GameObject.CreatePrimitive", FX)
        self.assertNotIn("AddComponent<Rigidbody>", FX.split("private CasingSlot CreateCasingSlot()",1)[1])

    def test_bot_gun_class_changes_flash_and_ejection_without_more_objects(self):
        self.assertIn("rivalGunfire.EmitForGun(shooter, impact, gun);", RIVAL)
        part = FX.split("internal void EmitForGun(", 1)[1].split(
            "private void Update()", 1)[0]
        for required in (
            'gun.category == "Pistol" ? .63f',
            'gun.category == "SMG" ? .79f',
            'gun.category == "Sniper" ? 1.25f',
            'gun.category == "DMR" ? 1.08f',
            'gun.category == "Shotgun" ? .31f',
            'gun.category == "Sniper" ? .29f',
            'casing.queued = true;',
            'casing.renderer.enabled = false;',
            'flash.flashPose.localScale *= scale;',
        ):
            self.assertIn(required, part)
        self.assertNotIn("new GameObject(", part)
        self.assertNotIn("AddComponent", part)
        self.assertNotIn("Physics.Raycast", part)
        self.assertIn("casing.queued && now >= casing.ejectAt", FX)
        self.assertIn("casing.queued = false;", FX)
        native = (ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text()
        self.assertIn('"Delayed sniper brass ejects from prebuilt pool"', native)
        self.assertIn("botEffectColliders += filter.GetComponentsInChildren<Collider>(true).Length;", native)
        self.assertNotIn("bots.GetComponentsInChildren<Collider>(true).Length == 0", native)

    def test_bot_death_spawns_crate_before_destruction(self):
        death=RIVAL.split("private void OnDied(AarisOfflineHealth defeated)",1)[1].split(
            "private void OnDestroy()",1)[0]
        for item in ("if (capsule != null) capsule.enabled = false;",
                     "owner.DropRivalLoot(transform.position, carriedGun,",
                     "carriedMagazine, carriedReserve, medkits, remoteLootSeed);",
                     "owner.RivalDefeated(this);","Destroy(gameObject, 1.2f);"):
            self.assertIn(item,death)
        self.assertLess(death.index("owner.DropRivalLoot("),
                        death.index("Destroy(gameObject, 1.2f);"))
        self.assertIn("private const int MaxVisuals = 16;",CRATE)
        self.assertIn("new List<Drop>(100);",CRATE)
        self.assertIn("Time.time + 0.25f;",CRATE)
        self.assertIn("unusedVisuals.Pop() : CreateVisual();",CRATE)
        self.assertIn("Vertical loot marker",CRATE)
        self.assertIn("position.y = bestSupportY;",CRATE)

    def test_crowded_death_crate_chooses_static_floor_not_live_bot(self):
        spawn = CRATE.split("internal void Spawn(", 1)[1].split(
            "internal static bool HasPickupNear(", 1)[0]
        self.assertIn("private RaycastHit[] spawnGroundHits = new RaycastHit[16];", CRATE)
        self.assertIn("AarisPhysicsHitBuffer.Raycast(", spawn)
        self.assertIn("ref spawnGroundHits, 256,", spawn)
        self.assertIn("surface.attachedRigidbody != null", spawn)
        self.assertIn("surface.GetComponentInParent<CharacterController>() != null", spawn)
        self.assertIn("hit.normal.y <= 0.45f", spawn)
        self.assertIn("hit.point.y > position.y + 0.35f", spawn)
        self.assertIn("hit.point.y <= bestSupportY", spawn)
        self.assertIn("position.y = bestSupportY;", spawn)
        self.assertIn("bool emptyMarker = state == null && medkits <= 0;", spawn)
        self.assertLess(spawn.index("bool emptyMarker ="),
                        spawn.index("AarisPhysicsHitBuffer.Raycast("))
        fixture = (ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text()
        self.assertIn("Crate ignores living rival cap and anchors on real floor", fixture)
        self.assertIn("Depleted rival leaves a temporary empty death marker", fixture)
        self.assertIn("Expired empty marker is cleaned without phantom loot", fixture)

    def test_crate_hint_matches_actual_pickup_priority_and_range(self):
        self.assertIn("out string weapon, out float distance, out bool canPick", CRATE)
        self.assertIn("instance.Nearest(seeker.position, PickupRadius, true, 0, true)", CRATE)
        self.assertIn("canPick = inReach != null;", CRATE)
        self.assertIn("Drop drop = inReach ?? instance.Nearest", CRATE)
        self.assertIn("cratePickupReady || closest == null", HUD)
        self.assertIn('(cratePickupReady ? "  PICK" : "")', HUD)
        self.assertIn("instance.cachedPickupNear = false;", CRATE)

    def test_ground_weapon_pickup_cannot_peek_over_cover_in_prone(self):
        loot = read("AarisWeaponLootWorld.cs")
        self.assertIn("looter.TryGetComponent(out CharacterController body)", loot)
        self.assertIn("body.center.y - body.height * .5f", loot)
        self.assertIn("body.height * .65f, .33f, .95f", loot)
        self.assertIn("Vector3 eye = looter.position + Vector3.up * eyeHeight;", loot)
        self.assertNotIn("Vector3 eye = looter.position + Vector3.up * .95f;", loot)
        # A wall spanning y=0...0.52m at z=0.84-0.96m blocks
        # crouched/prone pickup but clears standing for a gun 1.8m away.
        for height in (1.8, 1.2, .5, 1.8, .5):
            eye = max(.33, min(.95, height * .65))
            segment_height_at_wall_back = eye + (.24-eye) * (.96/1.8)
            self.assertEqual(segment_height_at_wall_back > .52, height > 1.6)
        native = (ROOT / "Assets/AarisWorld/Editor/AarisBotCombatIntegrationQA.cs").read_text()
        self.assertIn("Ground gun pickup obeys standing/crouch/prone cover", native)
        self.assertIn("float[] heights = {1.8f, 1.2f, .5f, 1.8f, .5f};", native)
        self.assertIn("capsule.height = heights[i];", native)

    def test_crate_reach_ray_obeys_live_stance_capsule(self):
        self.assertIn("private CharacterController playerCapsule;", CRATE)
        self.assertIn("playerCapsule = target != null", CRATE)
        self.assertIn("if (playerCapsule == null && player != null)", CRATE)
        self.assertIn("player.TryGetComponent(out playerCapsule);", CRATE)
        self.assertIn("playerCapsule.center.y -", CRATE)
        self.assertIn("playerCapsule.height * .65f", CRATE)
        self.assertIn("Vector3 origin = from + Vector3.up * rayLift;", CRATE)
        # Midpoint of a real 0.58m wall: standing ray clears, while
        # crouch and prone rays hit. A return to standing must clear again.
        heights = [1.8, 1.2, .5, 1.8, .5]
        for height in heights:
            origin_height = max(.33, min(1.02, height * .65))
            half_path_height = (origin_height + .32) * .5
            self.assertEqual(half_path_height > .58, height > 1.6)
        qa = (ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text()
        self.assertIn("Low cover obeys standing/crouch/prone LOS height=", qa)
        self.assertIn("lowCoverCollider.enabled = false;", qa)

    def test_full_or_absent_medical_inventory_cannot_claim_pickable_crate(self):
        medical = read("AarisOfflineTacticalHUD.cs")
        self.assertIn("internal bool CanReceiveDeathCrateMedkits =>", medical)
        self.assertIn("AcceptsTacticalInput && medkits < maxMedkits", medical)
        self.assertIn("int excludeProbe = 0, bool actionableOnly = false", CRATE)
        self.assertIn("medical.CanReceiveDeathCrateMedkits", CRATE)
        self.assertIn("bool takeMedkits = drop.medkits > 0 && canTakeMedkits;", CRATE)
        self.assertIn("Nearest(here, PickupRadius, true, 0, true)", CRATE)
        self.assertIn("Nearest(seeker.position, PickupRadius, true, 0, true)", CRATE)
        fixture = (ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text()
        self.assertIn("Uncollectable medkit-only crate never promises PICK", fixture)
        self.assertIn("Actionable crate hint bypasses nearer unavailable medkit", fixture)

    def test_full_slots_without_floor_world_only_allow_real_ammo_or_meds(self):
        self.assertIn("internal bool HasReserveSpace", GUN)
        self.assertIn("internal bool CanPickupDeathCrateGun =>", INVENTORY)
        self.assertIn("lootWorld != null && lootWorld.isActiveAndEnabled", INVENTORY)
        self.assertIn("internal bool CanReceiveDeathCrateAmmo(AarisWeaponSpec spec)", INVENTORY)
        self.assertIn("equipped.HasReserveSpace", INVENTORY)
        self.assertIn("stowed.HasReserveSpace", INVENTORY)
        self.assertIn("inventory.CanPickupDeathCrateGun;", CRATE)
        self.assertIn("bool takeGun = drop.gun != null && canTakeGun;", CRATE)
        self.assertIn("bool takeAmmo = drop.gun != null && drop.looseAmmo > 0 &&", CRATE)
        self.assertIn("if (!takeGun && !takeAmmo && !takeMedkits) continue;", CRATE)
        self.assertIn("drop.gun != null && inventory.CanPickupDeathCrateGun", CRATE)
        self.assertIn('drop.gun.Spec.displayName + " AMMO" : "MEDKIT"', CRATE)
        self.assertIn("true, probe, true);", CRATE)

    def test_visual_budget_keeps_closest_crates_when_saturated(self):
        world = CRATE.split("private void Update()", 1)[1].split(
            "private void Release(Drop drop)", 1)[0]
        self.assertIn("Drop furthestVisible = null;", world)
        self.assertIn("if (liveVisuals >= MaxVisuals)", world)
        self.assertIn("Release(furthestVisible);", world)
        self.assertIn("bestDistance + 0.25f >= furthestVisualDistance", world)
        self.assertLess(world.index("Release(furthestVisible);"),
                        world.index("CreateVisual();"))
        self.assertIn("nextPoll = Time.time + 0.25f;", world)

    def test_uncollectable_nearer_crate_does_not_block_other_pickup(self):
        self.assertIn("internal int lastPickupProbe;", CRATE)
        self.assertIn("private int pickupProbeSerial;", CRATE)
        take = CRATE.split("internal static bool TryLootNearest(",1)[1].split(
            "// Check line of sight only",1)[0]
        self.assertIn("int probe = ++instance.pickupProbeSerial;", take)
        self.assertIn("int attempts = instance.drops.Count;", take)
        self.assertIn("drop.lastPickupProbe = probe;", take)
        self.assertIn("instance.Nearest(seeker.position, PickupRadius,", take)
        self.assertIn("true, probe, true);", take)
        self.assertIn("if (drop == null) break;", take)
        self.assertIn("if (taken)", take)
        self.assertIn("return true;", take)
        self.assertIn("return false;", take)
        select = CRATE.split("private Drop Nearest(",1)[1].split(
            "private void Update()",1)[0]
        self.assertIn("int excludeProbe = 0", select)
        self.assertIn("drop.lastPickupProbe == excludeProbe", select)
        self.assertIn("if (mustReach && !Reachable(point, drop.position))", select)
        self.assertNotIn("new List<", take)
        self.assertNotIn("new GameObject(", take)

    def test_death_crate_visuals_share_lit_cube_mesh_without_colliders(self):
        self.assertIn("private static Mesh beamMesh, crateCubeMesh;", CRATE)
        mesh = CRATE.split("private static Mesh CrateCubeMesh()",1)[1].split(
            "private static void Part(",1)[0]
        self.assertIn("if (crateCubeMesh != null) return crateCubeMesh;", mesh)
        self.assertIn("var vertices = new Vector3[24];", mesh)
        self.assertIn("var triangles = new int[36];", mesh)
        self.assertIn("result.normals = normals;", mesh)
        self.assertIn("result.uv = uv;", mesh)
        self.assertIn("result.UploadMeshData(true);", mesh)
        part = CRATE.split("private static void Part(",1)[1].split(
            "private static Mesh BeamGeometry()",1)[0]
        self.assertIn("new GameObject(label, typeof(MeshFilter), typeof(MeshRenderer))", part)
        self.assertIn("part.GetComponent<MeshFilter>().sharedMesh = CrateCubeMesh();", part)
        self.assertIn("renderer.sharedMaterial = material;", part)
        self.assertIn("renderer.shadowCastingMode = ShadowCastingMode.Off;", part)
        self.assertNotIn("GameObject.CreatePrimitive", part)
        self.assertNotIn("GetComponent<Collider>", part)
        self.assertNotIn("AddComponent<Collider>", part)

    def test_full_slot_exchange_reserves_drop_and_rolls_back_on_failure(self):
        exchange = INVENTORY.split("internal bool TryPickupDeathCrateGun(",1)[1].split(
            "internal int AddDeathCrateAmmo",1)[0]
        self.assertIn("if (RequiresExchangeDrop)", exchange)
        self.assertIn("lootWorld.TryExchangeDeathCrateGun(incoming)", exchange)
        self.assertIn("displaced == null;", exchange)
        method = read("AarisWeaponLootWorld.cs").split(
            "internal bool TryExchangeDeathCrateGun(",1)[1].split(
            "public bool TryDropEquipped()",1)[0]
        self.assertIn("!HasLandingAuthority", method)
        self.assertIn("player != inventory.transform", method)
        self.assertIn("!TryFindDropSpot(out Vector3 point, requireReachable: true)",method)
        self.assertIn("deferVisual: true", method)
        self.assertLess(method.index("CreateDropped(outgoing, point"),
                        method.index("inventory.TryPickup(incoming,"))
        self.assertIn("UndoDrop(reserved);", method)
        self.assertIn("if (reserved == null) return false;", method)
        self.assertIn("requestPickupUntil = 0f;", method)

    def test_ammo_remainder_goes_to_matching_stowed_weapon(self):
        take = INVENTORY.split("internal int AddDeathCrateAmmo(",1)[1].split(
            "public bool DropCurrent()",1)[0]
        self.assertIn("added += equipped.AddReserve(amount);",take)
        self.assertIn("added += stowed.AddReserve(amount - added);",take)
        self.assertIn("return added;",take)
        self.assertLess(take.index("equipped.AddReserve(amount)"),
                        take.index("stowed.AddReserve(amount - added)"))

    def test_spent_medkits_do_not_respawn_in_death_crates(self):
        spawn = CRATE.split("internal void Spawn(",1)[1].split(
            "internal static bool HasPickupNear",1)[0]
        self.assertIn("bool emptyMarker = state == null && medkits <= 0;",spawn)
        self.assertIn("markerUntil = emptyMarker ? Time.time + 18f : 0f",spawn)
        self.assertIn("if (!drop.HasContent", CRATE)
        self.assertIn("medkits = Mathf.Max(0, medkits)",spawn)
        self.assertNotIn("Mathf.Max(1, medkits)",spawn)

    def test_atomic_pickup_cannot_destroy_full_inventory_weapon(self):
        take=CRATE.split("internal static bool TryLootNearest(",1)[1].split(
            "// Check line of sight only",1)[0]
        self.assertIn("inventory.TryPickupDeathCrateGun(transfer)",take)
        self.assertIn("drop.gun = null;",take)
        self.assertLess(take.index("inventory.TryPickupDeathCrateGun(transfer)"),
                        take.index("drop.gun = null;"))
        self.assertIn("inventory.AddDeathCrateAmmo",take)
        self.assertIn("medical.AddDeathCrateMedkits",take)
        self.assertIn("drop.looseAmmo -= added;",take)
        self.assertIn("drop.medkits -= added;",take)
        self.assertIn("mustReach && !Reachable(point, drop.position)",CRATE)
        self.assertIn("if (AarisDeathCrateWorld.TryLootNearest(transform, this)) return;",INVENTORY)
        self.assertIn("if (AcceptsPlayerWeaponInput && lootWorld != null)",INVENTORY)
        self.assertIn("internal int AddDeathCrateMedkits(int amount)",MEDICAL)
        self.assertIn("maxMedkits - medkits",MEDICAL)
        self.assertIn("AarisDeathCrateWorld.HasPickupNear(transform)",INVENTORY)
        self.assertIn("AarisDeathCrateWorld.TryGetNearbyHint",HUD)


    def test_nearby_bot_barrel_and_ejection_follow_real_weapon_length(self):
        # A fixed .52m emitter made the 23.5cm pistol look like a rifle
        # and the 1.23m sniper's flash appear inside its long barrel.
        self.assertIn("EmitWithBarrel(shooter, target, shotgun, .52f, .22f);", FX)
        self.assertIn("gun.presentationLengthMetres * .60f, .27f, .78f", FX)
        self.assertIn("gun.presentationLengthMetres * .25f, .075f, .34f", FX)
        self.assertIn("shooter.forward * barrelReach;", FX)
        self.assertIn("shooter.forward * reach + shooter.right * .18f;", FX)
        self.assertIn("casing.pose.position = EjectionPort(shooter, portReach);", FX)
        self.assertIn("EmitWithBarrel(shooter, target, gun.category == \"Shotgun\"", FX)
        lengths = {w["id"]: w["presentationLengthMetres"]
                   for w in CAT["weapons"]}
        muzzle = {k: min(.78, max(.27, v * .60))
                  for k, v in lengths.items()}
        port = {k: min(.34, max(.075, v * .25))
                for k, v in lengths.items()}
        self.assertLess(muzzle["Pistol"], muzzle["SMG"])
        self.assertLess(muzzle["SMG"], muzzle["AssaultRifle"])
        self.assertLess(muzzle["AssaultRifle"], muzzle["Sniper"])
        self.assertLess(port["Pistol"], port["Sniper"])
        # Same six pooled bot flashes/twelve shells; no new per-shot objects.
        self.assertIn("private const int MaximumSlots = 6;", FX)
        self.assertIn("private const int MaximumCasingSlots = 12;", FX)
        shot = FX.split("private void EmitWithBarrel(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertNotIn("new GameObject(", shot)
        self.assertNotIn("AddComponent", shot)



    def test_delayed_bot_brass_uses_live_receiver_and_one_ejection_ground_probe(self):
        # Sniper/shotgun receiver may travel between the muzzle report and
        # the pump/bolt's mechanical ejection deadline.
        self.assertIn("private void BeginCasingFlight(CasingSlot casing)", FX)
        self.assertIn("Transform shooter = casing.shooter;", FX)
        self.assertIn("port = EjectionPort(shooter, casing.portReach);", FX)
        self.assertIn("casing.pose.position = port;", FX)
        self.assertIn("casing.velocity = shooter.right * casing.sideSpeed", FX)
        self.assertIn("casing.shooter = shooter;", FX)
        self.assertIn("casing.portReach = portReach;", FX)
        self.assertIn("delay > 0f);", FX)
        shot = FX.split("private void EmitWithBarrel(", 1)[1].split(
            "// Weapon-specific bot presentation", 1)[0]
        self.assertIn("if (!delayedCasing) BeginCasingFlight(casing);", shot)
        update = FX.split("private void Update()", 1)[1]
        self.assertIn("casing.queued && now >= casing.ejectAt", update)
        self.assertIn("BeginCasingFlight(casing);", update)
        self.assertLess(update.index("casing.queued = false;"),
                        update.index("BeginCasingFlight(casing);"))
        self.assertLess(update.index("BeginCasingFlight(casing);"),
                        update.index("casing.renderer.enabled = true;"))
        # The call is in a one-time shell launch method, not the flight loop.
        self.assertEqual(FX.count("AarisEquippedGun.TryFindCasingGround("), 1)
        self.assertNotIn("Physics.Raycast(", update)

    def test_bot_shell_variation_does_not_mutate_combat_random_stream(self):
        self.assertIn("private uint casingVariation = 0x9E3779B9u;", FX)
        self.assertIn("private float NextCasingVariation()", FX)
        self.assertIn("unchecked(casingVariation * 1664525u + 1013904223u)", FX)
        shot = FX.split("private void EmitWithBarrel(", 1)[1].split(
            "// Weapon-specific bot presentation", 1)[0]
        self.assertIn("casing.sideSpeed = 2.05f + NextCasingVariation()", shot)
        self.assertIn("casing.upSpeed = 1.35f + NextCasingVariation()", shot)
        self.assertIn("casing.backSpeed = .12f + NextCasingVariation()", shot)
        self.assertIn("casing.spin = new Vector3(", shot)
        self.assertNotIn("UnityEngine.Random.Range", shot)
        self.assertNotIn("new GameObject(", shot)
        self.assertIn("Nearby bot cartridge ejections have distinct ballistic velocities",
                      (ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text())
        self.assertIn("Delayed bolt brass originates at moving shooter with actual floor",
                      (ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text())



    def test_player_shell_visual_rng_is_independent_from_combat_random(self):
        fire = GUN.split("public bool TryFire(", 1)[1].split(
            "private static bool IsAuthoredHeadHit", 1)[0]
        eject = GUN.split("private void EjectCasingNow()", 1)[1].split(
            "private void HideCasing()", 1)[0]
        self.assertIn("casingVisualSeed = state.SpreadSeed ^ 0xA15C0DEu;", GUN)
        self.assertIn("private float NextCasingVisualRange(", GUN)
        self.assertIn("unchecked(", GUN)
        self.assertIn("casingVisualSeed * 1664525u + 1013904223u", GUN)
        self.assertGreaterEqual(eject.count("NextCasingVisualRange("), 7)
        self.assertNotIn("UnityEngine.Random.Range(", eject)
        self.assertNotIn("Random.InitState(", eject)
        self.assertIn("AarisWeaponRandom.Next01(ref spreadSeed)", fire)
        self.assertIn("spreadSeed = state.SpreadSeed;", GUN)
        self.assertNotIn("spreadSeed =", eject)
        native = (ROOT / "Assets/AarisWorld/Editor/AarisProGunplayQA.cs").read_text("utf-8")
        self.assertIn("Player brass visuals do not mutate global combat RNG", native)
        self.assertIn("Independent player shells keep natural ballistic variation", native)
        self.assertIn("JsonUtility.ToJson(UnityEngine.Random.state)", native)

    def test_saturated_firefight_audio_never_discards_higher_priority_cues(self):
        self.assertIn("private static int ScheduledCuePriority(string key)", AUDIO)
        priority = AUDIO.split("private static int ScheduledCuePriority(", 1)[1].split(
            "private void Schedule(", 1)[0]
        self.assertIn('key == "grenade_throw" ? 2', priority)
        self.assertIn('key == "bolt_reload" ? 1 : 0', priority)
        scheduler = AUDIO.split("private void Schedule(", 1)[1].split(
            "private void PlayScheduled()", 1)[0]
        for expected in (
            "int lowestPriority = int.MaxValue;",
            "float latestAt = float.NegativeInfinity;",
            "ScheduledCuePriority(scheduled[i].Key)",
            "scheduled[i].At > latestAt",
            "incomingPriority < lowestPriority",
            "incomingPriority == lowestPriority && at >= latestAt",
            "nextScheduledAt = float.PositiveInfinity;",
            "nextScheduledAt = Mathf.Min(nextScheduledAt, at);",
        ):
            self.assertIn(expected, scheduler)
        for forbidden in ("new List<", "new GameObject(", "Instantiate(", "Physics.Raycast", "OrderBy("):
            self.assertNotIn(forbidden, scheduler)

        def rank(key):
            return {"grenade_throw": 2, "bolt_reload": 1}.get(key, 0)

        def add(queue, incoming):
            # Faithfully model the 16-slot fixed-array replacement policy.
            if len(queue) < 16:
                return queue + [incoming]
            worst = min(rank(cue[0]) for cue in queue)
            latest_at = max(cue[1] for cue in queue if rank(cue[0]) == worst)
            if rank(incoming[0]) < worst or (
                rank(incoming[0]) == worst and incoming[1] >= latest_at
            ):
                return queue
            index = next(i for i, cue in enumerate(queue)
                         if rank(cue[0]) == worst and cue[1] == latest_at)
            updated = list(queue)
            updated[index] = incoming
            return updated

        early = [("shell_drop", round(0.2 + i * 0.01, 2)) for i in range(16)]
        self.assertEqual(add(early, ("shell_drop", 1.4)), early)
        closer = add(early, ("shell_drop", 0.19))
        self.assertIn(("shell_drop", 0.19), closer)
        self.assertIn(("shell_drop", 0.2), closer)
        self.assertNotIn(("shell_drop", 0.35), closer)
        with_grenade = add(early, ("grenade_throw", 0.9))
        self.assertIn(("grenade_throw", 0.9), with_grenade)
        with_bolt = add(with_grenade, ("bolt_reload", 0.75))
        self.assertIn(("grenade_throw", 0.9), with_bolt)
        self.assertIn(("bolt_reload", 0.75), with_bolt)
        self.assertEqual(add([("grenade_throw", 1 + i) for i in range(16)],
                             ("shell_drop", 0.1)),
                         [("grenade_throw", 1 + i) for i in range(16)])

    def test_rival_gunfire_and_delayed_cues_are_directional_3d(self):
        bot_fire = RIVAL.split("float fireInterval = carriedGun != null", 1)[1].split(
            "private float NextVirtualRandom()", 1)[0]
        self.assertIn("isPlayerShot: false", bot_fire)
        self.assertIn("bool isPlayerShot = true", AUDIO)
        self.assertIn("if (forceWorld) local = false;", AUDIO)
        self.assertIn("bool local = IsLocalCue(name);", AUDIO)
        self.assertIn("bool forceWorld = false", AUDIO)
        self.assertIn("forceWorld: !isPlayerShot", AUDIO)
        gunshot = AUDIO.split("public static void GunShot(", 1)[1].split(
            "public static void BulletImpact(", 1)[0]
        self.assertIn("if (!isPlayerShot &&", gunshot)
        self.assertIn("(position - instance.transform.position).sqrMagnitude > 70f * 70f", gunshot)
        self.assertLess(gunshot.index("if (!isPlayerShot &&"),
                        gunshot.index("instance.OneShot(sound, position"))
        self.assertIn("public bool Local;", AUDIO)
        self.assertIn("Local = local", AUDIO)
        self.assertIn("forceWorld: !cue.Local", AUDIO)
        gunshot = AUDIO.split("public static void GunShot(", 1)[1].split(
            "public static void BulletImpact(", 1)[0]
        self.assertIn("local: isPlayerShot", gunshot)
        self.assertIn("private const int LocalVoiceCount = 4;", AUDIO)
        self.assertIn("private const int VoiceCount = 12;", AUDIO)
        # No per-bot sound source or duplicate global voice manager.
        self.assertNotIn("new AudioSource", gunshot)
        self.assertNotIn("AddComponent<AudioSource>", gunshot)
        self.assertNotIn("Instantiate(", gunshot)

    def test_brass_drop_audio_never_precedes_real_ejection(self):
        gunshot = AUDIO.split("public static void GunShot(", 1)[1].split(
            "public static void BulletImpact(", 1)[0]
        self.assertIn("pitch * instance.NextGunshotPitch()", gunshot)
        self.assertNotIn("UnityEngine.Random.Range(", gunshot)
        self.assertIn("private float NextGunshotPitch()", AUDIO)
        self.assertIn('category == "Shotgun" ? .31f', gunshot)
        self.assertIn('category == "Sniper" ? .29f : .025f', gunshot)
        self.assertIn('instance.Schedule("shell_drop", position, casingDelay + .63f,', gunshot)
        self.assertIn('.16f, local: isPlayerShot);', gunshot)
        player_ejection = GUN.split("private void ScheduleCasingEjection()", 1)[1].split(
            "private void TickCasingEjections()", 1)[0]
        for category, expected in (("Shotgun", 0.31), ("Sniper", 0.29), ("SMG", 0.025)):
            self.assertIn(f'spec.category == "{category}"', player_ejection) if category != "SMG" else None
            self.assertGreater(expected + 0.63, expected)
            self.assertLess(expected + 0.63, 1.0)
        # Fixed-size queue and pooled voices are unchanged.
        self.assertIn("private readonly TimedCue[] scheduled = new TimedCue[16];", AUDIO)
        self.assertIn("private const int VoiceCount = 12;", AUDIO)

if __name__ == "__main__":
    unittest.main()
