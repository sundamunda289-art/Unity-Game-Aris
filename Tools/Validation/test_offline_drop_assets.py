#!/usr/bin/env python3
"""License-free static checks for bundled original offline BR models and scripts.

These checks are NOT a substitute for a licensed Unity compilation, physics
test, native scene render, Android profiling or Play Mode verification.
"""
from pathlib import Path
import json
import math
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "Assets" / "Resources"
SCRIPTS = ROOT / "Assets" / "AarisWorld" / "Scripts"

MODELS = {
    "aircraft": (ASSETS / "AarisDrop" / "CargoPlane.obj", 25, 5, 19),
    "parachute": (ASSETS / "AarisDrop" / "ElongatedParachute.obj", 8, 0, 0),
    "gloo": (ASSETS / "AarisCombat" / "GlooWall.obj", 3.6, 2.1, 0.2),
    "frag": (ASSETS / "AarisCombat" / "FragGrenade.obj", 0.18, 0.22, 0),
}


def read_obj(path):
    verts, faces, used = [], [], set()
    material = None
    for line in path.read_text(encoding="utf-8").splitlines():
        words = line.strip().split()
        if not words:
            continue
        if words[0] == "v":
            vertex = tuple(float(x) for x in words[1:4])
            if len(vertex) != 3 or not all(math.isfinite(x) for x in vertex):
                raise ValueError(f"non-finite OBJ vertex: {path}")
            verts.append(vertex)
        elif words[0] == "usemtl":
            material = words[1]
            used.add(material)
        elif words[0] == "f":
            indices = [int(x.split("/")[0]) for x in words[1:]]
            if len(indices) != 3:
                raise ValueError(f"Only triangles allowed in {path}")
            faces.append(indices)
    if not verts or not faces:
        raise ValueError(f"Empty OBJ mesh: {path}")
    for face in faces:
        if not all(1 <= index <= len(verts) for index in face):
            raise ValueError(f"OBJ index outside vertex range in {path}")
    sizes = [max(v[a] for v in verts) - min(v[a] for v in verts) for a in range(3)]
    return verts, faces, used, sizes


class OfflineCombatAssetChecks(unittest.TestCase):
    def test_original_3d_models_are_complete_and_local(self):
        for name, (path, min_x, min_y, min_z) in MODELS.items():
            with self.subTest(model=name):
                self.assertTrue(path.is_file(), str(path))
                self.assertTrue(path.with_suffix(".mtl").is_file())
                self.assertTrue((Path(str(path) + ".meta")).is_file())
                self.assertTrue((Path(str(path.with_suffix(".mtl")) + ".meta")).is_file())
                lines = path.read_text(encoding="utf-8").splitlines()
                self.assertIn(f"mtllib {path.stem}.mtl", lines)
                vertices, faces, materials, bounds = read_obj(path)
                self.assertGreaterEqual(len(faces), 100)
                self.assertTrue(materials)
                self.assertGreater(bounds[0], min_x)
                self.assertGreater(bounds[1], min_y)
                self.assertGreater(bounds[2], min_z)

    def test_player_safe_footing_uses_same_world_space_ground_height(self):
        player = (SCRIPTS / "AarisThirdPersonPlayer.cs").read_text("utf-8")
        self.assertIn("ground = Mathf.Max(ground, t.SampleHeight(position));", player)
        self.assertNotIn("origin.y + t.SampleHeight(position)", player)
        self.assertIn("if (GroundHeight(position) >= seaLevel + 0.35f)", player)

    def test_mobile_jump_and_prone_movement_safety(self):
        motor = (SCRIPTS / "AarisThirdPersonPlayer.cs").read_text("utf-8")
        stance = (SCRIPTS / "AarisBattleActions.cs").read_text("utf-8")
        self.assertIn("HasWalkableGroundWithinStep()", motor)
        self.assertIn("!wasGrounded && verticalVelocity <= 0f", motor)
        self.assertIn("now - lastJumpStarted > 0.18f", motor)
        self.assertIn("now - lastGrounded <= 0.15f", motor)
        self.assertIn("jumpUntil = Time.unscaledTime + 0.20f;", motor)
        self.assertIn("if (groundedBeforeStand) lastGrounded = Time.unscaledTime;", motor)
        self.assertIn("ProneWallSlide(horizontalVelocity, dt)", motor)
        self.assertIn("SafeFooting(next) && battle.CanAdvanceProne(next, test)", motor)
        self.assertIn("private float jumpHeight = 1.35f, gravity = 18.5f;", motor)
        self.assertIn("private float earlyJumpReleaseGravity = 1.35f;", motor)
        self.assertIn("stance == PlayerStance.Crouching ? 0.20f : 0.16f", stance)
        self.assertIn("if (height > capsule.height && !HasRoomAbove(height)) return false;", stance)

    def test_animation_stance_weights_remain_exclusive_and_prone_moves(self):
        source = (SCRIPTS / "AarisCharacterVisual.cs").read_text("utf-8")
        self.assertIn("AnimationMixerPlayable.Create(graph, 12)", source)
        self.assertIn("float stanceTotal = crouchBlend + proneBlend;", source)
        self.assertIn("float stanceScale = stanceTotal > 1f ? pose / stanceTotal : 1f;", source)
        self.assertIn("float crouchWeight = authoredProne ? crouchBlend * stanceScale : pose;", source)
        self.assertIn("float proneWeight = authoredProne ? proneBlend * stanceScale : 0f;", source)
        self.assertIn("float grounded = (1f - airborneBlend) * (1f - pose);", source)
        self.assertIn("WriteWeightIfChanged(mixer, lastBaseWeights, 5, baseWeight * crouchWeight", source)
        self.assertIn("WriteWeightIfChanged(mixer, lastBaseWeights, 6, baseWeight * crouchWeight", source)
        self.assertIn("WriteWeightIfChanged(mixer, lastBaseWeights, 10, baseWeight * proneWeight", source)
        self.assertIn("WriteWeightIfChanged(mixer, lastBaseWeights, 11, baseWeight * proneWeight", source)
        self.assertIn("LoopPlayable(crawlPlayable, crawlClip, crawlPhase);", source)
        self.assertIn("LoopPlayable(sprintPlayable, sprintClip, locomotionPhase);", source)
        # Authored prone is entirely clip driven. The old bone-pulse hack must
        # not fight the retargeted crawl or spend time mutating limb bones.
        self.assertNotIn("crawlLeftArm.localRotation *=", source)
        self.assertNotIn("crawlRightLeg.localRotation *=", source)
        # Exercise rapid/overlapping stance transitions as well as steady
        # standing, crouch and prone. Each twelve-input base mixer must
        # preserve unity total even if Crouch + Prone briefly exceeds one.
        for crouch in (0, .2, .5, 1):
            for prone in (0, .2, .5, 1):
                for airborne in (0, .15, .55, 1):
                    for speed in (0, .2, .85, 1.4):
                        total = crouch + prone
                        pose = min(1., total)
                        scale = pose / total if total > 1. else 1.
                        standing = (1-airborne) * (1-pose)
                        air = airborne * (1-pose)
                        for authored_prone in (False, True):
                            cw = crouch * scale if authored_prone else pose
                            pw = prone * scale if authored_prone else 0.
                            cm = min(1., max(0., (speed-.06)/(.85-.06)))
                            pm = min(1., max(0., (speed-.05)/(.55-.05)))
                            crouch_idle, crouch_walk = cw*(1-cm), cw*cm
                            prone_idle, crawl = pw*(1-pm), pw*pm
                            self.assertAlmostEqual(standing+air+crouch_idle+
                                crouch_walk+prone_idle+crawl,1.,places=7)

    def test_kaykit_cc0_motion_provenance_and_guid_pairs(self):
        folder = ASSETS / "AarisCharacters" / "Primary"
        clips = json.loads((folder / "Animations" / "kaykit-clip-sources.json").read_text("utf-8"))
        self.assertEqual(clips["license"], "CC0-1.0")
        self.assertIn("CC0", (folder / "LICENSE-KAYKIT-ANIMATIONS.txt").read_text("utf-8"))
        motions = ("walk", "run", "sprint", "crouch", "crouch_walk", "prone", "crawl", "punch")
        self.assertEqual(set(clips["clips"]), set(motions))
        for motion in motions:
            fbx = folder / "Animations" / (motion + ".fbx")
            meta = folder / "Animations" / (motion + ".fbx.meta")
            self.assertTrue(fbx.is_file(), motion)
            self.assertTrue(meta.is_file(), motion)
            self.assertRegex(meta.read_text("utf-8"), r"(?m)^guid: [0-9a-f]{32}$")
            pointer = fbx.read_bytes()
            if pointer.startswith(b"version https://git-lfs.github.com/spec/v1"):
                oid = re.search(rb"(?m)^oid sha256:([0-9a-f]{64})$", pointer)
                self.assertIsNotNone(oid, motion)
                self.assertEqual(oid.group(1).decode(), clips["clips"][motion]["sha256"])

    def test_weapon_fps_swap_and_damage_gc_regression(self):
        catalog = (SCRIPTS / "AarisWeaponCatalog.cs").read_text("utf-8")
        inventory = (SCRIPTS / "AarisWeaponInventory.cs").read_text("utf-8")
        pickup = (SCRIPTS / "AarisWeaponPickup.cs").read_text("utf-8")
        world = (SCRIPTS / "AarisWeaponLootWorld.cs").read_text("utf-8")
        combat = (SCRIPTS / "AarisEquippedGun.cs").read_text("utf-8")
        self.assertIn("private static readonly Dictionary<string, GameObject> modelCache", catalog)
        self.assertIn("public static GameObject ModelFor(AarisWeaponSpec spec)", catalog)
        self.assertIn("AarisWeaponCatalog.ModelFor(incoming.Spec)", inventory)
        self.assertIn("AarisWeaponCatalog.ModelFor(state.Spec)", pickup)
        self.assertIn("public bool TryReserve()", pickup)
        self.assertIn("public void ReleaseReservation()", pickup)
        self.assertIn("public void CommitConsumed()", pickup)
        self.assertIn("!pickup.TryReserve()", world)
        self.assertIn("pickup.ReleaseReservation();", world)
        self.assertIn("pickup.CommitConsumed();", world)
        self.assertIn("deferVisual: true", world)
        self.assertIn("private const int MaximumActiveLoot = 40;", world)
        self.assertIn("CollectNearbyCandidateIndices(playerPosition);", world)
        self.assertIn("damageScratch.Clear();", combat)
        self.assertIn("node.GetComponents(damageScratch)", combat)
        self.assertIn("Physics.RaycastNonAlloc", combat)
        self.assertNotIn("node.GetComponents<MonoBehaviour>()", combat)
        self.assertNotIn("Vector3[] attempts =", world)

    def test_vehicle_exit_resets_physics_without_damaging_validated_floor(self):
        session = (SCRIPTS / "AarisVehicleSession.cs").read_text("utf-8")
        player = (SCRIPTS / "AarisThirdPersonPlayer.cs").read_text("utf-8")
        self.assertIn("footMotor.ResetCameraAfterVehicle();", session)
        self.assertIn("Physics.OverlapCapsuleNonAlloc", session)
        self.assertIn("Physics.RaycastNonAlloc", session)
        self.assertNotIn("Vector3[] options =", session)
        after = player.split("public void ResetCameraAfterVehicle()", 1)[1]
        before_reset = after.split("private void ResetPlayer()", 1)[0]
        for token in ("verticalVelocity = -groundedDownVelocity;",
                      "horizontalVelocity = Vector3.zero;",
                      "lastGrounded = Time.unscaledTime;",
                      "lastJumpStarted = jumpUntil = -10f;"):
            self.assertIn(token, before_reset)
    
    def test_world_y_not_double_added_to_drop_altitude(self):
        source = (SCRIPTS / "AarisOfflineDrop.cs").read_text(encoding="utf-8")
        self.assertIn("tallest = Mathf.Max(tallest, terrain.SampleHeight(", source)
        self.assertIn("support = Mathf.Max(support, t.SampleHeight(position));", source)
        self.assertNotIn("terrain.SampleHeight(new Vector3(x, 0f, z)) + p.y", source)
        self.assertNotIn("p.y + t.SampleHeight(position)", source)

    def test_replacement_cargo_plane_has_swept_four_engine_art(self):
        verts, faces, materials, size = read_obj(MODELS["aircraft"][0])
        self.assertGreaterEqual(len(faces), 750)
        self.assertLessEqual(len(faces), 2400, "Avoid mobile overdraw and oversized plane geometry")
        self.assertLessEqual(len(verts), 1300)
        self.assertGreater(size[0], 31)
        self.assertGreater(size[1], 7)
        self.assertGreater(size[2], 22)
        self.assertTrue({"HulledMetal", "DarkCockpit", "WingPanels",
                         "EngineDark", "SafetyAccent"}.issubset(materials))
        source = (SCRIPTS / "AarisOfflineDropVisuals.cs").read_text("utf-8")
        movement = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        self.assertIn("BuildPropellerMesh()", source)
        self.assertIn("else if (slots.Length == 5)", source)
        self.assertIn("AARIS_CARGO_PLANE_MODEL_NOT_IMPORTED", source)
        self.assertIn("Quaternion.Euler(0f, 0f,", movement)
        self.assertIn("Time.unscaledDeltaTime", movement)
        self.assertNotIn('"PropellerBladeA"', source)
        self.assertNotIn('"PropellerBladeB"', source)

    def test_parachute_is_elongated_and_round_ended_not_rectangular(self):
        verts, _, _, bounds = read_obj(MODELS["parachute"][0])
        self.assertGreater(bounds[0] / max(bounds[2], 0.1), 2.8)
        near_tip = [abs(z) for x, y, z in verts if abs(x) > 4.3]
        center = [abs(z) for x, y, z in verts if abs(x) < 0.25]
        self.assertTrue(near_tip and center)
        self.assertLess(max(near_tip), max(center) * 0.65)

    def test_interactive_door_never_hits_approaching_player_and_closes_safely(self):
        door = (SCRIPTS / "AarisBreakablePanel.cs").read_text("utf-8")
        self.assertIn("house.InverseTransformPoint(player.position).z", door)
        self.assertIn("openDirection = house.InverseTransformPoint(player.position).z <", door)
        self.assertIn("shouldOpen ? OpenAngle * openDirection : 0f", door)
        self.assertIn("Mathf.Abs(currentAngle) > 80f", door)
        self.assertIn("Mathf.Abs(currentAngle) > .01f && ClosingBlocked()", door)
        self.assertIn("Physics.OverlapBoxNonAlloc", door)
        self.assertIn("if (hits >= blockers.Length) return true;", door)
        self.assertIn("QueryTriggerInteraction.Ignore", door)

    def test_saved_loot_and_car_resources_have_full_map_coverage(self):
        # Asset-backed coverage check only: physics-spawn and device play still
        # need the native Unity player to validate real clearance.
        loot = json.loads((ASSETS / "AarisWeapons" / "loot_candidates.json").read_text("utf-8"))
        points = json.loads((ASSETS / "AarisVehicles" / "vehicle_candidates.json").read_text("utf-8"))
        cars = json.loads((ASSETS / "AarisVehicles" / "vehicle_catalog.json").read_text("utf-8"))
        self.assertEqual(loot["sizeMetres"], 2000)
        self.assertEqual(points["sizeMetres"], 2000)
        self.assertEqual(points["hubCount"], 45)
        self.assertGreaterEqual(len(loot["candidates"]), 400)
        self.assertGreaterEqual(len(points["candidates"]), 70)
        self.assertGreaterEqual(len(cars["vehicles"]), 3)
        self.assertTrue(any(p["kind"] == "indoor" for p in loot["candidates"]))
        self.assertTrue(any(p["kind"] != "indoor" for p in loot["candidates"]))
        for vehicle in cars["vehicles"]:
            for resource in (vehicle["modelResource"], vehicle["wheelResource"]):
                model = ASSETS / (resource + ".fbx")
                self.assertTrue(model.is_file(), str(model))
                self.assertTrue(Path(str(model) + ".meta").is_file(), str(model))
        source = (SCRIPTS / "AarisVehicleWorld.cs").read_text("utf-8")
        self.assertIn("MaximumNearbyVehicles = 10", source)
        self.assertIn("Physics.OverlapBoxNonAlloc", source)
        self.assertIn("TryResolve(record, out Vector3 pos)", source)
        door = (SCRIPTS / "AarisBreakablePanel.cs").read_text("utf-8")
        self.assertIn("ClosingBlocked()", door)
        self.assertIn("Physics.OverlapBoxNonAlloc", door)
        self.assertIn("NearRadius = 3.2f", door)
        self.assertIn("solid.enabled = false", door)
        modular = (SCRIPTS / "AarisModularHouse.cs").read_text("utf-8")
        self.assertIn("AutomaticBreakableDoorHinge", modular)
        self.assertIn("RealDoorLeaf", modular)

    def test_script_api_and_damage_compatibility(self):
        contents = {
            key: (SCRIPTS / key).read_text(encoding="utf-8")
            for key in [
                "AarisOfflineDrop.cs", "AarisOfflineDropVisuals.cs",
                "AarisOfflineGlooWall.cs", "AarisOfflineFragGrenade.cs",
                "AarisOfflineHealth.cs", "AarisOfflineTacticalHUD.cs",
                "AarisThirdPersonPlayer.cs", "AarisEquippedGun.cs"
            ]
        }
        self.assertIn("ResumeFromAerialDrop()", contents["AarisThirdPersonPlayer.cs"])
        self.assertIn("footMotor.ResumeFromAerialDrop()", contents["AarisOfflineDrop.cs"])
        self.assertIn("CollisionFlags.Below", contents["AarisOfflineDrop.cs"])
        self.assertIn("Terrain.activeTerrains", contents["AarisOfflineDrop.cs"])
        self.assertIn("IAarisDamageable", contents["AarisEquippedGun.cs"])
        self.assertIn("IAarisDamageable", contents["AarisOfflineHealth.cs"])
        self.assertIn("IAarisDamageable", contents["AarisOfflineGlooWall.cs"])
        self.assertIn("Physics.OverlapBoxNonAlloc", contents["AarisOfflineGlooWall.cs"])
        self.assertIn("MaterialPropertyBlock", contents["AarisOfflineGlooWall.cs"])
        self.assertIn("Physics.OverlapSphereNonAlloc", contents["AarisOfflineFragGrenade.cs"])
        for name in ("CargoPlane", "ElongatedParachute", "GlooWall", "FragGrenade"):
            self.assertTrue(any(name in source for source in contents.values()), name)
        for key, source in contents.items():
            self.assertNotIn("NetworkBehaviour", source)
            self.assertNotIn("Unity.Netcode", source)
            self.assertNotIn("https://", source)
            self.assertNotIn("http://", source)

    def test_aerial_camera_collision_and_native_terrain_clearance(self):
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        helper = drop[drop.index("private Vector3 ClampCameraAboveTerrain("):
                      drop.index("private void LateUpdate()")]
        camera = drop[drop.index("private void LateUpdate()"):
                      drop.index("private void RestoreFootControls()")]
        self.assertIn("tile.SampleHeight(point) + 0.8f", helper)
        self.assertNotIn("tile.transform.position.y + tile.SampleHeight", helper)
        self.assertIn("if ((next - pos).sqrMagnitude > 0.00000001f)", camera)
        self.assertIn("next = CameraSafePosition(focus, next);", camera)
        self.assertIn("next = ClampCameraAboveTerrain(next);", camera)
        self.assertLess(camera.index("next = CameraSafePosition(focus, next);"),
                        camera.index("next = ClampCameraAboveTerrain(next);"))
        self.assertLess(camera.index("next = ClampCameraAboveTerrain(next);"),
                        camera.index("view.transform.position = next;"))

    def test_pc_combat_keys_do_not_trigger_two_actions(self):
        # Regression: V used to aim AND deploy cover; G dropped the gun
        # while throwing a grenade. Mobile touch actions remain unchanged.
        wall = (SCRIPTS / "AarisOfflineGlooWall.cs").read_text("utf-8")
        tactical = (SCRIPTS / "AarisOfflineTacticalHUD.cs").read_text("utf-8")
        weapons = (SCRIPTS / "AarisWeaponInventory.cs").read_text("utf-8")
        bindings = {
            "cover": re.search(r"Input\.GetKeyDown\(KeyCode\.(\w+)\)\) BeginAim", wall),
            "frag": re.search(r"Input\.GetKeyDown\(KeyCode\.(\w+)\)\) ThrowFrag", tactical),
            "aim": re.search(r"Input\.GetKeyDown\(KeyCode\.(\w+)\)\) ToggleAim", weapons),
            "drop": re.search(r"Input\.GetKeyDown\(KeyCode\.(\w+)\)\) DropCurrent", weapons),
        }
        for action, match in bindings.items():
            self.assertIsNotNone(match, f"Missing desktop input for {action}")
        keys = {name: match.group(1) for name, match in bindings.items()}
        self.assertEqual(keys, {"cover": "B", "frag": "Q", "aim": "V", "drop": "G"})
        self.assertEqual(len(set(keys.values())), len(keys))
        self.assertIn("Input.GetKeyUp(KeyCode.B)", wall)

    def test_wall_touch_hold_recovers_after_vehicle_and_focus_change(self):
        tactical = (SCRIPTS / "AarisOfflineTacticalHUD.cs").read_text("utf-8")
        cover = (SCRIPTS / "AarisOfflineGlooWall.cs").read_text("utf-8")
        self.assertIn("if (wallTouch != null) wallTouch.CancelHold();", tactical)
        self.assertIn("holding = int.MinValue;", tactical)
        self.assertIn("if (paused) CancelHold();", tactical)
        self.assertIn("if (!focused) CancelHold();", tactical)
        self.assertIn("if (paused) CancelAim();", cover)

    def test_cover_damage_ignores_nan_and_infinity(self):
        wall = (SCRIPTS / "AarisOfflineGlooWall.cs").read_text("utf-8")
        self.assertIn("float.IsNaN(amount)", wall)
        self.assertIn("float.IsInfinity(amount)", wall)

    def test_offline_healing_rejects_non_finite_amounts(self):
        health = (SCRIPTS / "AarisOfflineHealth.cs").read_text("utf-8")
        restore = health.split("public void RestoreHealth(float amount)", 1)[1]
        restore = restore.split("}", 1)[0]
        self.assertIn("float.IsNaN(amount)", restore)
        self.assertIn("float.IsInfinity(amount)", restore)

    def test_frag_blast_obstacles_not_filtered_as_damage_targets(self):
        frag = (SCRIPTS / "AarisOfflineFragGrenade.cs").read_text("utf-8")
        # Candidate filtering is intentional; collision occlusion needs world geometry.
        self.assertIn("overlaps, damageMask, QueryTriggerInteraction.Ignore", frag)
        self.assertIn("length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore", frag)
        self.assertNotIn("length, damageMask, QueryTriggerInteraction.Ignore", frag)


    def test_new_unity_guids_are_distinct(self):
        files = [
            *ASSETS.joinpath("AarisDrop").glob("*.meta"),
            *ASSETS.joinpath("AarisCombat").glob("*.meta"),
            *SCRIPTS.glob("AarisOffline*.cs.meta")
        ]
        found = set()
        for path in files:
            content = path.read_text(encoding="utf-8")
            match = re.search(r"^guid:\s*([a-f0-9]{32})$", content, re.M)
            self.assertIsNotNone(match, str(path))
            self.assertNotIn(match.group(1), found, str(path))
            found.add(match.group(1))


if __name__ == "__main__":
    unittest.main(verbosity=2)
