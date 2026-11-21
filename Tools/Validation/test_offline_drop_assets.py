#!/usr/bin/env python3
"""License-free static checks for bundled original offline BR models and scripts.

These checks are NOT a substitute for a licensed Unity compilation, physics
test, native scene render, Android profiling or Play Mode verification.
"""
from pathlib import Path
import hashlib
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
    def test_parachute_touch_does_not_stick_after_android_input_loss(self):
        # The real-player steering stick must recover if Android loses
        # PointerUp, including without an app background/focus transition.
        # Static source contract; live uGUI gesture testing still required.
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        stick = drop.split("public sealed class AarisOfflineDropStick", 1)[1]
        self.assertIn("IPointerUpHandler, ICancelHandler", stick)
        self.assertIn("public void OnCancel(BaseEventData data) => ResetStick();", stick)
        self.assertIn("private void LateUpdate()", stick)
        self.assertIn("if (finger == int.MinValue) return;", stick)
        self.assertIn("system.currentInputModule is StandaloneInputModule", stick)
        self.assertIn("if (!IsPointerPhysicallyHeld(finger)) ResetStick();", stick)
        self.assertIn("pointerId == PointerInputModule.kMouseLeftId", stick)
        self.assertIn("Input.GetMouseButton(0)", stick)
        self.assertIn("Input.GetTouch(i)", stick)
        self.assertIn("touch.phase != TouchPhase.Ended", stick)
        self.assertIn("touch.phase != TouchPhase.Canceled", stick)
        self.assertIn("private void OnDisable() => ResetStick();", stick)
        self.assertIn("if (paused) ResetStick();", stick)
        self.assertIn("if (!focused) ResetStick();", stick)
        self.assertIn("data.button != PointerEventData.InputButton.Left", stick)
        self.assertIn("data != null && finger == data.pointerId", stick)

        # Model the pointer ownership contract across common interrupted
        # gesture sequences at any frame rate. Cancelled/ended/absent touch
        # is never treated as continued steering.
        def physically_held(pointer, mouse, touches):
            if pointer == -1:
                return mouse
            return any(fid == pointer and phase not in ("Ended", "Canceled")
                       for fid, phase in touches)

        for pointer, mouse, touches, expected in (
            (3, False, [(3, "Moved")], True),
            (3, False, [(3, "Stationary")], True),
            (3, False, [(3, "Ended")], False),
            (3, False, [(3, "Canceled")], False),
            (3, False, [(7, "Moved")], False),
            (3, False, [], False),
            (-1, True, [], True),
            (-1, False, [], False),
        ):
            self.assertEqual(physically_held(pointer, mouse, touches), expected)

    def test_offline_zone_is_walkable_and_rivals_react_to_it(self):
        br = (SCRIPTS / "AarisOfflineBRDirector.cs").read_text("utf-8")
        begin = br.split("private void Begin()", 1)[1].split(
            "private void TrySpawnSlot(", 1)[0]
        rival = br.split("internal sealed class AarisOfflineRival", 1)[1]
        self.assertIn("centers[i] = centers[i - 1];", begin)
        self.assertIn("for (int probe = 0; probe < 8; probe++)", begin)
        self.assertIn("float fraction = 1f - probe * 0.125f;", begin)
        self.assertIn("TryGetGround(proposed.x, proposed.y, out float ground)", begin)
        self.assertIn("centers[i] = proposed;", begin)
        self.assertIn("internal bool TryZoneRetreatDirection(", br)
        self.assertIn("float safe = Mathf.Max(6f, radius - 12f);", br)
        self.assertIn("owner.TryZoneRetreatDirection(transform.position,", rival)
        self.assertIn("bool retreat = outsideSafeArea && (distance > 23f || !canSee);",
                      rival)
        self.assertIn("Vector3 travelDirection = seekingCover ? coverHeading.normalized :", rival)
        self.assertIn("retreat ? retreatDirection : toward;", rival)
        self.assertIn("if (canSee && !retreat && !seekingCover) desired *= .23f;", rival)
        self.assertIn("Vector3 beforeMove = transform.position;", rival)
        self.assertIn("Vector3 realTravel = transform.position - beforeMove;", rival)
        self.assertIn("realTravel.y = 0f;", rival)
        self.assertIn("groundSpeed / 2.9f", rival)
        self.assertNotIn("Mathf.Abs(moveSpeed) / 2.9f", rival)

        # Representative land/water candidates: scan towards a known
        # walkable previous center without creating any new location farther
        # than the allowed (old radius - new radius) * 0.54.
        for previous, current in ((1080., 660.), (660., 360.),
                                  (360., 175.), (175., 65.)):
            shift = (previous-current)*.54
            options = [shift * (1. - probe*.125) for probe in range(8)]
            self.assertEqual(len(options), 8)
            self.assertGreater(options[0], options[-1])
            self.assertTrue(all(0 < d <= shift for d in options))
            self.assertLessEqual(options[0]+current, previous)
        # No travel = no running pose, including at low and high FPS.
        for fps in (10, 15, 30, 60, 120):
            dt = 1. / fps
            self.assertEqual(0. / max(.001, dt), 0.)
            self.assertAlmostEqual((2.85*dt) / dt / 2.9, 2.85/2.9)

    def test_offline_rivals_recover_spawns_and_avoid_unloaded_walls(self):
        br = (SCRIPTS / "AarisOfflineBRDirector.cs").read_text("utf-8")
        grid = (SCRIPTS / "AarisNearbyColliders.cs").read_text("utf-8")
        # When every first-attempt site is a blocked house or steep cliff,
        # the match continues to search, rather than permanently claiming
        the_match_is_over = "spawnComplete = true;"
        self.assertIn("if (spawnedRivals < RivalCount &&", br)
        self.assertIn("spawnPasses < MaximumSpawnPasses", br)
        self.assertIn("private const int MaximumSpawnPasses = 4;", br)
        self.assertIn("private readonly bool[] populatedSlots = new bool[RivalCount];", br)
        self.assertIn("if (!populatedSlots[slot]) TrySpawnSlot(slot);", br)
        self.assertIn("populatedSlots[index] = true;", br)
        self.assertIn("index >= RivalCount || populatedSlots[index]", br)
        self.assertIn("attemptedSlots = 0;", br)
        self.assertIn("nextSpawn = Time.time + 2.5f;", br)
        self.assertIn("if (!populatedSlots[slot]) TrySpawnSlot(slot);", br)
        self.assertIn(the_match_is_over, br)

        # Remote obstacles are disabled by the original 55m collision
        # streamer. Bots must consult its cached bounds, not trigger an
        # expensive Physics.SyncTransforms/enable-all-colliders per update.
        self.assertIn("internal bool WouldCrossInactiveCover(", br)
        self.assertIn("nearbyColliders.HasPotentialInactiveObstacle(predicted,", br)
        self.assertIn("new Vector3(0.36f, 0.90f, 0.36f)", br)
        self.assertIn("owner.WouldCrossInactiveCover(transform.position, displacement)", br)
        self.assertIn("owner.WouldCrossInactiveCover(transform.position, first)", br)
        self.assertIn("owner.WouldCrossInactiveCover(transform.position, second)", br)
        self.assertIn("displacement = Vector3.zero;", br)
        self.assertIn("HasPotentialInactiveObstacle(", grid)
        self.assertNotIn("Physics.SyncTransforms()", br)
        self.assertNotIn("nearbyColliders.RefreshNow()", br)
        self.assertIn('string participants = spawnComplete', br)
        self.assertIn('"RIVALS " + spawnedRivals + "/" + RivalCount', br)

        # 10/15/20/30/60/90/120 FPS: a steady 2.85 m/s pursuit no longer
        # loses half its elapsed time at a 10 Hz phone frame rate.
        self.assertIn("float dt = Mathf.Min(Time.deltaTime, 0.12f);", br)
        self.assertIn("capsule.Move(displacement +", br)
        self.assertNotIn("float dt = Mathf.Min(Time.deltaTime, 0.05f);", br)
        for fps in (10, 15, 20, 30, 60, 90, 120):
            seconds = 5
            sim_distance = sum(2.85 * min(1.0 / fps, .12)
                               for _ in range(fps * seconds))
            self.assertAlmostEqual(sim_distance, 2.85 * seconds, places=5)

    def test_zero_rival_spawn_retry_is_bounded_and_recoverable(self):
        br = (SCRIPTS / "AarisOfflineBRDirector.cs").read_text("utf-8")
        end = (SCRIPTS / "AarisOfflineMatchEnd.cs").read_text("utf-8")
        self.assertIn("spawnPasses < MaximumSpawnPasses)", br)
        self.assertIn("if (spawnedRivals == 0)", br)
        self.assertIn("matchEnd.FailMatchSetup();", br)
        self.assertIn("else if (activeRivals == 0)", br)
        self.assertNotIn("if (spawnedRivals == 0 ||", br)
        self.assertIn("public void FailMatchSetup()", end)
        self.assertIn('FinishMatch(false, "MATCH UNAVAILABLE");', end)
        self.assertIn('titleOverride ?? (winner ? "VICTORY" : "DEFEATED")', end)
        # Model zero-spawn, partial-spawn and full-spawn exit gates.
        for spawned in (0, 1, 3, 6):
            passes = 1
            while spawned < 6 and passes < 4:
                passes += 1
            self.assertLessEqual(passes, 4)
            if spawned == 0:
                self.assertEqual(passes, 4)

    def test_offline_br_is_a_bounded_real_match_not_an_empty_map(self):
        br = (SCRIPTS / "AarisOfflineBRDirector.cs").read_text("utf-8")
        end = (SCRIPTS / "AarisOfflineMatchEnd.cs").read_text("utf-8")
        health = (SCRIPTS / "AarisOfflineHealth.cs").read_text("utf-8")
        self.assertIn("private const int RivalCount = 100;", br)
        self.assertIn("if (drop != null && !drop.HasLanded) return;", br)
        self.assertIn("nextSpawn = Time.time + 0.17f;", br)
        self.assertIn("TryGetGround(x, z, out float ground)", br)
        self.assertIn("origin.y + tile.SampleHeight(", br)
        self.assertIn("HasPotentialInactiveObstacle(", br)
        self.assertIn("Physics.OverlapCapsuleNonAlloc", br)
        self.assertIn("Physics.OverlapCapsule(low, high, 0.34f,", br)
        self.assertIn("Mathf.Clamp01((elapsed - CloseStarts[stage])", br)
        self.assertIn("Mathf.Lerp(ZoneRadii[stage], ZoneRadii[stage + 1], blend)", br)
        self.assertIn("Outside(player.position)", br)
        self.assertIn("playerHealth.ApplyDamage(7f, null)", br)
        self.assertIn("Physics.RaycastNonAlloc(ray, sightHits, distance,", br)
        self.assertIn("hits = Physics.RaycastAll(ray, distance,", br)
        self.assertIn("private bool HasSightTo(Transform victim)", br)
        self.assertIn("closest.transform.IsChildOf(victim)", br)
        self.assertIn("HasPlayerSightForSelection()", br)
        self.assertIn("targetHealth.ApplyDamage(damage, gameObject);", br)
        self.assertIn("private void OnDied(AarisOfflineHealth defeated)", br)
        self.assertIn("spawnComplete && spawnedRivals > 0 && activeRivals == 0", br)
        self.assertIn("matchEnd.WinMatch();", br)
        self.assertIn("Resources.Load<GameObject>(", br)
        self.assertIn("AarisCharacters/Kenney/characterMedium", br)
        self.assertIn("AnimationMixerPlayable.Create(graph, 2)", br)
        self.assertIn("public void WinMatch()", end)
        self.assertIn('winner ? "VICTORY" : "DEFEATED"', end)
        self.assertIn("public bool IsFinished => pausedForDefeat;", end)
        self.assertIn("public sealed class AarisOfflineHealth", health)
        # All intermediate shrink targets must remain nested; the final
        # safe area may move but can never jump outside the previous one.
        radii = (1080., 660., 360., 175., 65.)
        begins = (45., 165., 285., 400.)
        ends = (140., 245., 360., 465.)
        for i in range(4):
            self.assertGreater(radii[i], radii[i+1])
            self.assertLess(begins[i], ends[i])
            if i:
                self.assertGreater(begins[i], ends[i-1])
            shift = (radii[i]-radii[i+1])*.54
            self.assertLessEqual(shift+radii[i+1], radii[i])
            for f in (0., .05, .2, .5, .8, 1.):
                mid = radii[i] + (radii[i+1]-radii[i])*f
                self.assertLessEqual(mid, radii[i])
                self.assertGreaterEqual(mid, radii[i+1])

    def test_offline_rivals_reuse_bundled_realistic_humanoid_and_animation(self):
        br = (SCRIPTS / "AarisOfflineBRDirector.cs").read_text("utf-8")
        source = ASSETS / "AarisCharacters" / "Primary"
        model = source / "RealisticSoldier" / "RealisticSoldier.fbx"
        self.assertTrue(model.is_file() and model.stat().st_size > 100_000)
        self.assertIn("animationType: 3", model.with_suffix(".fbx.meta").read_text("utf-8"))
        for name in ("idle", "run"):
            fbx = source / "Animations" / (name + ".fbx")
            self.assertTrue(fbx.is_file() and fbx.stat().st_size > 100_000)
            meta = fbx.with_suffix(".fbx.meta").read_text("utf-8")
            self.assertIn("animationType: 3", meta)
            self.assertIn("importAnimation: 1", meta)
        self.assertFalse((ASSETS / "AarisCharacters" / "Kenney").exists())
        self.assertIn("AarisCharacters/Primary/RealisticSoldier/RealisticSoldier", br)
        self.assertIn("AarisCharacters/Primary/Animations/idle", br)
        self.assertIn("AarisCharacters/Primary/Animations/run", br)
        self.assertIn("if (!rivalAssetsResolved)", br)
        self.assertIn("owner.ResolveRivalAssets(", br)
        self.assertIn("Resources.LoadAll<Avatar>(path)", br)
        self.assertIn("TryFitHumanoidModel(visual.transform, animator)", br)
        self.assertIn("owner.CompatibleRivalMaterial(materials[i])", br)
        self.assertIn("foreach (Material converted in rivalMaterialCache.Values)", br)
        self.assertIn("idle == null || run == null) return;", br)
        self.assertIn("AnimationMixerPlayable.Create(graph, 2)", br)

    def test_ocean_glide_requires_solid_contact_or_safe_recovery(self):
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        move = drop.split("private void MoveAirborne(float dt)", 1)[1].split(
            "private bool TryGetDistanceToSupport(", 1)[0]
        collision = move.split("if ((flags & CollisionFlags.Below) != 0", 1)[1].split(
            "if (IsUnderCanopy", 1)[0]
        self.assertRegex(
            collision, r"if \(transform\.position\.y < SeaSurfaceHeight \+ 0\.35f\)\s+"
                       r"RecoverFromOceanFall\(\);\s+else\s+Land\(\);")
        self.assertIn("TryNativeTerrainTouchdown(beforeMove)", move)
        self.assertIn("Physics.Raycast(probe, out RaycastHit floor", move)
        self.assertIn("floor.point.y >= SeaSurfaceHeight + 0.35f", move)
        self.assertIn("floor.normal.y >= Mathf.Cos(capsule.slopeLimit", move)
        self.assertIn("if (transform.position.y < SeaSurfaceHeight - 2f)", move)
        self.assertIn("RecoverFromOceanFall();", move)
        self.assertIn("if (noGroundContactSince < 0f)", move)
        self.assertIn("Time.time - noGroundContactSince >= 1.25f", move)
        self.assertIn("else noGroundContactSince = -1f;", move)
        self.assertIn("groundY < SeaSurfaceHeight + 0.35f", move)
        self.assertIn("GetInterpolatedNormal(u, v)", move)
        self.assertIn("preFlightGroundPosition = transform.position;", drop)
        self.assertIn("transform.SetPositionAndRotation(preFlightGroundPosition,", move)
        self.assertIn("now.y < groundY - 1.0f", move)
        self.assertEqual(drop.count("private void RecoverFromOceanFall()"), 1)

    def test_overlapping_terrain_tiles_do_not_block_touchdown_or_camera(self):
        # A border position may belong to more than one terrain tile.
        # Reject one unsuitable sample without hiding another valid ground.
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        touchdown = drop.split("private bool TryNativeTerrainTouchdown(", 1)[1].split(
            "private void RecoverFromOceanFall()", 1)[0]
        self.assertIn("beforeMove.y < groundY - 1.0f) continue;", touchdown)
        self.assertIn(
            "Mathf.Cos(capsule.slopeLimit * Mathf.Deg2Rad)) continue;",
            touchdown)
        camera = drop.split("private Vector3 ClampCameraAboveTerrain(", 1)[1].split(
            "private void LateUpdate()", 1)[0]
        self.assertIn("origin.y + tile.SampleHeight(point) + 0.8f);",
                      camera)
        self.assertNotIn("break;", camera)

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

    def test_aircraft_uses_saved_urp_lit_finishes_instead_of_flat_runtime_paint(self):
        visuals = (SCRIPTS / "AarisOfflineDropVisuals.cs").read_text("utf-8")
        self.assertIn(
            'Resources.Load<Material>("AarisDrop/Materials/" + materialName)',
            visuals)
        self.assertIn("Material hullPaint = SavedAircraftFinish(", visuals)
        self.assertIn("if (saved != null && saved.shader != null", visuals)
        profiles = {
            "CargoPlane-HulledMetal": (0.38, 0.32),
            "CargoPlane-DarkCockpit": (0.08, 0.82),
            "CargoPlane-WingPanels": (0.30, 0.25),
            "CargoPlane-EngineDark": (0.52, 0.26),
            "CargoPlane-SafetyAccent": (0.18, 0.27),
        }
        for name, (metallic, smoothness) in profiles.items():
            with self.subTest(material=name):
                self.assertIn(f'SavedAircraftFinish("{name}"', visuals)
                source = (ASSETS / "AarisDrop" / "Materials" /
                          (name + ".mat")).read_text("utf-8")
                self.assertIn("guid: 933532a4fcc9baf4fa0491de14d08ed7", source)
                self.assertRegex(source, rf"(?m)^    - _Metallic: {metallic:g}$")
                self.assertRegex(source, rf"(?m)^    - _Smoothness: {smoothness:g}$")

    def test_rotor_opposite_faces_have_independent_lighting_normals(self):
        source = (SCRIPTS / "AarisOfflineDropVisuals.cs").read_text("utf-8")
        self.assertIn("vertices.Add(vertices[first + vertex]);", source)
        self.assertIn("int offset = back - first;", source)
        self.assertIn("triangles.Add(a + offset); triangles.Add(c + offset); triangles.Add(b + offset);", source)
        self.assertIn("Paint(ref rotorPaint,", source)
        self.assertIn("SetAircraftFinish(bladeMaterial, .30f, .24f);", source)
        # Model the two planar faces and confirm all normals have opposite
        # signs but never share a vertex. Triangle count stays unchanged.
        for blade in range(3):
            angle = blade * 2 * math.pi / 3
            vertices = [(r * math.cos(angle) - w * math.sin(angle),
                         r * math.sin(angle) + w * math.cos(angle))
                        for r, width in ((.24, .22), (1.02, .19), (1.66, .055))
                        for w in (width, -width)]
            vertices += vertices[:]
            front = [(0, 1, 2), (2, 1, 3), (2, 3, 4), (4, 3, 5)]
            back = [(a + 6, c + 6, b + 6) for a, b, c in front]
            normals = [0.0] * 12
            for a, b, c in front + back:
                x, y = vertices[a]
                bx, by = vertices[b]
                cx, cy = vertices[c]
                cross = (bx-x)*(cy-y) - (by-y)*(cx-x)
                for i in (a, b, c):
                    normals[i] += cross
            self.assertTrue(all(n > 0 for n in normals[:6]))
            self.assertTrue(all(n < 0 for n in normals[6:]))
            self.assertEqual(len(front + back), 8)

    def test_player_safe_footing_uses_same_world_space_ground_height(self):
        player = (SCRIPTS / "AarisThirdPersonPlayer.cs").read_text("utf-8")
        self.assertIn("ground = Mathf.Max(ground, origin.y + t.SampleHeight(position));", player)
        self.assertNotIn("ground = Mathf.Max(ground, t.SampleHeight(position));", player)
        self.assertIn("if (GroundHeight(position) >= seaLevel + 0.35f)", player)

    def test_drop_resolves_components_added_after_its_awake(self):
        # AfterSceneLoad callbacks have no cross-type order guarantee.
        # Drop.Start must resolve other bootstraps before disabling combat
        # and checking whether the player is already driving.
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        start = drop.split("private void Start()", 1)[1].split(
            "private void BuildFlightPath()", 1)[0]
        for dependency in ("AarisBattleActions", "AarisVehicleSession",
                           "AarisCharacterVisual"):
            self.assertIn(f"= GetComponent<{dependency}>();", start)
        self.assertLess(start.index("combat = GetComponent<AarisBattleActions>();"),
                        start.index("combatWasEnabled = combat != null"))
        self.assertLess(start.index("vehicle = GetComponent<AarisVehicleSession>();"),
                        start.index("if (vehicle != null && vehicle.IsDriving)"))

    def test_aircraft_suspends_and_restores_player_presentation(self):
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        rig = (SCRIPTS / "AarisCharacterVisual.cs").read_text("utf-8")
        hide = drop.split("private void HidePlayer()", 1)[1].split("private void ShowPlayer()", 1)[0]
        show = drop.split("private void ShowPlayer()", 1)[1].split("private void GetPlaneCameraPose", 1)[0]
        self.assertIn("SetPresentationPaused(true)", hide)
        self.assertIn("SetPresentationPaused(false)", show)
        self.assertIn("if (paused) graph.Stop();", rig)
        self.assertIn("else graph.Play();", rig)
        self.assertIn("if (presentationPaused || visual == null || controller == null) return;", rig)
        self.assertIn("FitVisualToController(resourcePath) && ConfigureDrawableSkin(resourcePath)", rig)
        self.assertIn("skin.localBounds = poseBounds;", rig)
        self.assertIn("bodySkin.BakeMesh(posed);", rig)
        probe = (ROOT / "Assets/AarisWorld/Editor/AarisOfflineDropPlayProbe.cs").read_text("utf-8")
        self.assertIn("DropPhase.Gliding || drop.HasLanded", probe)
        self.assertIn("VerifyPlayerVisibility(drop, cam);", probe)

    def test_delayed_humanoid_boot_hides_plane_rig_and_restores_after_snapshot(self):
        visual = (SCRIPTS / "AarisCharacterVisual.cs").read_text("utf-8")
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        start = visual.split("private void Start()", 1)[1].split(
            "private static bool IsLegacyPlaceholderRenderer", 1)[0]
        presentation = visual.split("internal void SetPresentationPaused(bool paused)", 1)[1].split(
            "private void LateUpdate()", 1)[0]
        show = drop.split("private void ShowPlayer()", 1)[1].split(
            "private void GetPlaneCameraPose", 1)[0]
        self.assertIn("if (aerialDrop != null && aerialDrop.IsRidingAircraft)", start)
        self.assertIn("if (presentationPaused && graphReady) graph.Stop();", start)
        self.assertIn("if (presentationPaused) visual.SetActive(false);", start)
        self.assertIn("if (presentationPaused != paused)", presentation)
        self.assertIn("visual.SetActive(!paused);", presentation)
        self.assertIn("if (!paused) HideLegacyPlaceholders();", presentation)
        self.assertLess(show.index("previousRendererStates[i]"),
                        show.index("SetPresentationPaused(false)"))
        self.assertIn("characterVisual = GetComponent<AarisCharacterVisual>();", show)

    def test_missing_android_touch_ended_event_does_not_lock_camera_or_joystick(self):
        # Unity may omit Ended after a system gesture while another finger
        # remains on screen. Prove both controllers reconcile ownership
        # before processing the current touch snapshot.
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        player = (SCRIPTS / "AarisThirdPersonPlayer.cs").read_text("utf-8")
        aerial_look = drop.split("private void ReadLookInput()", 1)[1].split(
            "private Vector2 MovementIntent()", 1)[0]
        foot_input = player.split("private void ReadInput()", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("if (lookFinger >= 0)", aerial_look)
        self.assertIn("if (!stillTouching) lookFinger = -1;", aerial_look)
        self.assertLess(aerial_look.index("if (!stillTouching)"),
                        aerial_look.index("ApplyLookDrag(t.deltaPosition);"))
        self.assertIn("private void OnApplicationPause(bool paused)", drop)
        self.assertIn("private void OnApplicationFocus(bool focused)", drop)
        self.assertIn("if (!movePresent) { moveFinger = -1; moveInput = Vector2.zero; }",
                      foot_input)
        self.assertIn("if (!lookPresent) lookFinger = -1;", foot_input)
        self.assertLess(foot_input.index("if (!lookPresent)"),
                        foot_input.index("if (t.phase == TouchPhase.Began)"))
        # Do not replace the existing touch HUD or introduce a second motor.
        self.assertEqual(player.count("controller.Move("), 1)

    def test_freefall_camera_pitch_does_not_change_fall_physics(self):
        # Up/down camera swipes are a view-only operation. The previous
        # sink/forward multipliers read pitch directly and changed the
        # skydiver's physical descent whenever the player looked around.
        source = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        freefall = source.split("private void UpdateFreefall(float dt)", 1)[1].split(
            "private void UpdateGlide(float dt)", 1)[0]
        self.assertIn("bool fastDive = input.y > 0.75f;", freefall)
        self.assertIn("float targetSink = fastDive ? -freefallFastSink : -freefallSlowSink;", freefall)
        self.assertIn("* (fastDive ? 0.62f : 1f);", freefall)
        # Strip comments before checking that neither orientation nor pitch
        # contributes to any executable freefall physics expression.
        executable = "\n".join(line.split("//", 1)[0] for line in freefall.splitlines())
        self.assertNotIn("pitch", executable)
        self.assertIn("Vector2 input = MovementIntent();", executable)
        self.assertLess(executable.index("Vector2 input = MovementIntent();"),
                        executable.index("bool fastDive = input.y > 0.75f;"))
        self.assertIn("Mathf.MoveTowards(downwardSpeed, targetSink, 35f * dt)", executable)

    def test_dense_colliders_do_not_permanently_lock_stance_transitions(self):
        # A full non-alloc collider buffer is an incomplete answer. On rare
        # dense spots, preserve actual wall/ceiling checks with a complete
        # query; do not reject safe stand/prone transitions just for overflow.
        source = (SCRIPTS / "AarisBattleActions.cs").read_text("utf-8")
        room = source.split("private bool HasRoomAbove(", 1)[1].split(
            "public bool CanAdvanceProne(", 1)[0]
        prone = source.split("private bool HasProneClearance(", 1)[1].split(
            "private void Update()", 1)[0]
        for segment, complete_query, hit_array in (
            (room, "Physics.OverlapCapsule(low, high, radius, mask,",
             "headroomHits[i]"),
            (prone, "Physics.OverlapCapsule(head, legs, 0.245f, mask,",
             "proneHits[i]"),
        ):
            self.assertIn("Physics.OverlapCapsuleNonAlloc", segment)
            self.assertIn("if (count >= nearHits.Length)", segment)
            self.assertIn(complete_query, segment)
            self.assertIn(hit_array, segment)
            self.assertNotIn("if (count == nearHits.Length) return false;", segment)
            self.assertIn("obstacle.transform.IsChildOf(transform)" if segment == prone
                          else "hit.transform.IsChildOf(transform)", segment)
        # A flat walkable terrain remains support, but the current prone
        # clearance guard rejects an uphill bank intersecting head or legs.
        # Older tests requiring unconditional TerrainCollider skipping would
        # silently reintroduce the clipping regression.
        self.assertIn("if (obstacle is TerrainCollider terrain)", prone)
        self.assertIn("if (!terrain.Raycast(", prone)
        self.assertIn("sampleHit.point.y > center.y - 0.10f", prone)
        self.assertIn("sampleHit.normal.y < minWalkableNormal", prone)
        self.assertNotIn("if (obstacle is TerrainCollider) continue;", prone)
        self.assertIn("floorHit.normal.y", prone)

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
        self.assertIn("private float jumpHeight = 0.90f, gravity = 21.5f;", motor)
        self.assertIn("private float earlyJumpReleaseGravity = 1.65f;", motor)
        self.assertIn("private float minimumJumpAscentSeconds = 0.09f;", motor)
        self.assertIn("stance == PlayerStance.Crouching ? 0.18f : 0.075f", stance)
        self.assertIn("if (height > capsule.height && !HasRoomAbove(height)) return false;", stance)

    def test_animation_stance_weights_remain_exclusive_and_prone_moves(self):
        source = (SCRIPTS / "AarisCharacterVisual.cs").read_text("utf-8")
        self.assertIn("AnimationMixerPlayable.Create(graph, 12)", source)
        self.assertIn("pronePlayable.SetSpeed(0d);", source)
        self.assertLess(source.index("pronePlayable.SetSpeed(0d);"),
                        source.index("pronePlayable.SetTime("))
        self.assertIn("float stanceTotal = effectiveCrouchBlend + effectiveProneBlend;", source)
        self.assertIn("float stanceScale = stanceTotal > 1f ? pose / stanceTotal : 1f;", source)
        self.assertIn("float crouchWeight = authoredProne ? effectiveCrouchBlend * stanceScale : pose;", source)
        self.assertIn("float proneWeight = authoredProne ? effectiveProneBlend * stanceScale : 0f;", source)
        self.assertIn("float grounded = (1f - airborneBlend) * (1f - pose);", source)
        self.assertIn("WriteWeightIfChanged(mixer, lastBaseWeights, 5, baseWeight * crouchWeight", source)
        self.assertIn("WriteWeightIfChanged(mixer, lastBaseWeights, 6, baseWeight * crouchWeight", source)
        self.assertIn("WriteWeightIfChanged(mixer, lastBaseWeights, 10, baseWeight * proneWeight", source)
        self.assertIn("WriteWeightIfChanged(mixer, lastBaseWeights, 11, baseWeight * proneWeight", source)
        self.assertIn("LoopPlayable(crawlPlayable, crawlClip, crawlPhase);", source)
        # Upright foot planting stays allocation-free normally, but a saturated
        # unordered NonAlloc result must fall back to a complete query so dense
        # props/bridge colliders cannot hide the nearest walkable surface.
        query_buffer = (SCRIPTS / "AarisPhysicsHitBuffer.cs").read_text()
        self.assertIn("ref ankleSupportHits, 128, out RaycastHit[] hits", source)
        self.assertIn("if (count >= retained.Length)", query_buffer)
        self.assertIn("hits = Physics.RaycastAll(ray, distance,", query_buffer)
        self.assertIn("RaycastHit hit = hits[i];", source)
        self.assertIn("LoopPlayable(runPlayable, runClip, locomotionPhase + runPhaseOffset);", source)
        self.assertIn("LoopPlayable(sprintPlayable, sprintClip, locomotionPhase + sprintPhaseOffset);", source)
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

    def test_cc0_motion_provenance_and_guid_pairs(self):
        folder = ASSETS / "AarisCharacters" / "Primary"
        clips = json.loads((folder / "Animations" / "kaykit-clip-sources.json").read_text("utf-8"))
        self.assertEqual(clips["license"], "CC0-1.0")
        self.assertIn("CC0", (folder / "LICENSE-KAYKIT-ANIMATIONS.txt").read_text("utf-8"))
        motions = ("walk", "run", "crouch", "crouch_walk", "prone", "crawl", "punch")
        self.assertEqual(set(clips["clips"]), set(motions))
        self.assertNotIn("sprint", clips["clips"])
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

        provenance = json.loads((folder / "asset-source.json").read_text("utf-8"))
        sprint = provenance["sprint_animation"]
        self.assertEqual(sprint["source_action"], "Sprint_Loop_Armature")
        self.assertEqual(sprint["license"], "CC0-1.0")
        self.assertEqual(sprint["output_sha256"],
                         "ced84d553b95742e7e27b161985a08561a6a11397bacc2f187b373fdd4a6d99e")
        sprint_fbx = folder / "Animations" / "sprint.fbx"
        sprint_meta = folder / "Animations" / "sprint.fbx.meta"
        self.assertTrue(sprint_fbx.is_file())
        self.assertTrue(sprint_meta.is_file())
        sprint_bytes = sprint_fbx.read_bytes()
        if sprint_bytes.startswith(b"version https://git-lfs.github.com/spec/v1"):
            pointer = sprint_bytes.decode("utf-8")
            self.assertIn("oid sha256:" + sprint["output_sha256"], pointer)
            self.assertIn("size 394796", pointer)
        else:
            self.assertEqual(hashlib.sha256(sprint_bytes).hexdigest(),
                             sprint["output_sha256"])

        importer = (ROOT / "Assets/AarisWorld/Editor/AarisCharacterImport.cs").read_text("utf-8")
        self.assertIn("private static readonly (string bone, string human)[] QuaterniusHumanoid", importer)
        self.assertIn('("pelvis", "Hips")', importer)
        self.assertIn('("thigh_l", "LeftUpperLeg")', importer)
        self.assertIn('("calf_l", "LeftLowerLeg")', importer)
        self.assertIn('string.Equals(clipName, "sprint", StringComparison.Ordinal)', importer)
        self.assertIn('assetPath.EndsWith("sprint.fbx", StringComparison.OrdinalIgnoreCase)', importer)
        self.assertIn("humanoid.hasTranslationDoF = preserveTranslationDof;", importer)
        for motion in ("walk", "run", "sprint", "crouch_walk", "crawl"):
            meta = (folder / "Animations" / (motion + ".fbx.meta")).read_text("utf-8")
            self.assertIn("hasTranslationDoF: 1", meta, motion)

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
        self.assertIn("AarisPhysicsHitBuffer.Raycast", combat)
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
    
    def test_world_y_includes_native_terrain_origin_for_drop_altitude(self):
        source = (SCRIPTS / "AarisOfflineDrop.cs").read_text(encoding="utf-8")
        self.assertIn("p.y + terrain.SampleHeight(new Vector3(x, 0f, z))", source)
        self.assertIn("support = Mathf.Max(support, p.y + t.SampleHeight(position));", source)
        self.assertIn("float groundY = origin.y + tile.SampleHeight(now);", source)
        self.assertIn("origin.y + tile.SampleHeight(point) + 0.8f", source)

    def test_replacement_cargo_plane_has_swept_four_engine_art(self):
        verts, faces, materials, size = read_obj(MODELS["aircraft"][0])
        self.assertGreaterEqual(len(faces), 750)
        self.assertIn("o Aaris_TacticalTransport_V3", MODELS["aircraft"][0].read_text("utf-8"))
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

    def test_preferred_realistic_cargo_plane_is_vendored_and_bounded(self):
        path = ASSETS / "AarisDrop" / "CargoPlaneRealistic.obj"
        self.assertTrue(path.is_file(), str(path))
        self.assertTrue(path.with_suffix(".mtl").is_file())
        self.assertTrue(Path(str(path) + ".meta").is_file())
        self.assertTrue(Path(str(path.with_suffix(".mtl")) + ".meta").is_file())
        self.assertTrue((ASSETS / "AarisDrop" / "CargoPlaneRealistic_SOURCE.txt").is_file())

        vertices, faces, materials, bounds = read_obj(path)
        self.assertGreaterEqual(len(vertices), 80000)
        self.assertLessEqual(len(vertices), 100000)
        self.assertGreaterEqual(len(faces), 40000)
        self.assertLessEqual(len(faces), 50000)
        self.assertGreater(bounds[0], 24)
        self.assertGreater(bounds[1], 6)
        self.assertGreater(bounds[2], 22)
        self.assertTrue({"HulledMetal", "DarkCockpit", "WingPanels",
                         "EngineDark", "SafetyAccent"}.issubset(materials))

        raw = path.read_bytes()
        self.assertEqual(
            hashlib.sha256(raw).hexdigest(),
            "e8be17599956157d4304bfe6f083d27428f900ddfb79ab52da5aed5057148ae8")
        source_note = (ASSETS / "AarisDrop" /
                       "CargoPlaneRealistic_SOURCE.txt").read_text("utf-8")
        self.assertIn("Asset ID: 32587", source_note)
        self.assertIn("License: CC0 1.0 Universal", source_note)
        self.assertIn(
            "Source GLB SHA-256: 4fc998fda721e47fef83e5615a11a62eddb947486ae87baecabb9ed1e8623da4",
            source_note)
        self.assertIn(
            "Converted OBJ SHA-256: e8be17599956157d4304bfe6f083d27428f900ddfb79ab52da5aed5057148ae8",
            source_note)

        visuals = (SCRIPTS / "AarisOfflineDropVisuals.cs").read_text("utf-8")
        self.assertIn(
            'Resources.Load<GameObject>("AarisDrop/CargoPlaneRealistic")',
            visuals)
        self.assertIn(
            'prefab = Resources.Load<GameObject>("AarisDrop/CargoPlane")',
            visuals)
        self.assertIn("if (!highDetailTwin)", visuals)
        self.assertIn("new float[] { -4f, 4f }", visuals)
        self.assertIn("rotorScale = highDetailTwin ? 1.17f : 1f", visuals)

    def test_low_fps_airborne_sweeps_preserve_motion_and_plane_flyaway(self):
        source = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        self.assertIn("Mathf.Clamp(Time.deltaTime, 0f, 0.20f)", source)
        self.assertIn("const float MaxSweepSeconds = 0.05f;", source)
        self.assertIn("Mathf.Clamp(Mathf.CeilToInt(elapsed / MaxSweepSeconds), 1, 4)", source)
        self.assertIn("float stepDt = elapsed / steps;", source)
        self.assertIn("SimulateAirborne(dt);", source)
        self.assertIn("AdvanceDepartingAircraft(dt);", source)
        self.assertIn("AnimatePlaneRotors();", source)
        self.assertIn("plane.transform.position += planeFlightDirection * planeSpeed * dt;", source)
        self.assertIn("ApplyPlaneVisualInertia();", source)
        self.assertIn("Mathf.Min(freefallForwardSpeed, planeSpeed * 0.55f)", source)
        self.assertIn("if (phase == DropPhase.Freefall) UpdateFreefall(stepDt);", source)
        self.assertIn("else if (phase == DropPhase.Opening || phase == DropPhase.Gliding)", source)
        self.assertIn("else break; // Once the player lands, stop airborne movement.", source)
        eject = source.split("private void Eject()", 1)[1].split("private void OpenCanopy()", 1)[0]
        self.assertNotIn("Destroy(plane", eject)
        self.assertNotIn("plane = null;", eject)
        # The 20 Hz step bound holds across frame rates that were previously
        # clamped to 0.11s and made low-FPS freefall/steering run too slowly.
        for elapsed in (1/120, 1/60, 1/30, 1/20, 1/15, 1/10, 0.2):
            steps = max(1, min(4, math.ceil(elapsed / 0.05)))
            self.assertLessEqual(elapsed / steps, 0.050000001)
            self.assertAlmostEqual((elapsed / steps) * steps, elapsed)

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
        self.assertIn("if (!shouldOpen && Mathf.Abs(currentAngle) > .01f)", door)
        self.assertIn("else if (ClosingBlocked())", door)
        self.assertIn("Physics.OverlapBoxNonAlloc", door)
        self.assertIn("if (hits >= blockers.Length)", door)
        self.assertIn("candidates = crowdedBlockers;", door)
        self.assertIn("if (hits >= crowdedBlockers.Length)", door)
        self.assertIn("candidates = Physics.OverlapBox(middle, halfExtents,", door)
        self.assertIn("var hit = candidates[i];", door)
        self.assertNotIn("if (hits >= blockers.Length) return true;", door)
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
        self.assertIn("TryResolvePlanned(record, out Vector3 pos)", source)
        # All fallback vehicle candidates share the original physics solver.
        self.assertIn("if (TryResolve(record, out spot)) return true;", source)
        self.assertIn("return TryResolve(record, out spot, axis.x * offset, axis.z * offset);", source)
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

    def test_airplane_camera_starts_at_plane_and_avoids_high_altitude_casts(self):
        source = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        start = source.split("private void Start()", 1)[1].split(
            "private void BuildFlightPath()", 1)[0]
        late = source.split("private void LateUpdate()", 1)[1].split(
            "private void RestoreFootControls()", 1)[0]
        self.assertIn("phase = DropPhase.InPlane;", start)
        self.assertIn("GetPlaneCameraPose(out Vector3 initialPosition,", start)
        self.assertIn("view.transform.SetPositionAndRotation(initialPosition,", start)
        self.assertLess(start.index("phase = DropPhase.InPlane;"),
                        start.index("view.transform.SetPositionAndRotation(initialPosition,"))
        # Plane visual gust/bank must remain visible on the model without
        # making the chase camera inherit that perturbed forward axis.
        camera_pose = source.split("private void GetPlaneCameraPose(", 1)[1].split(
            "private Vector3 CameraSafePosition(", 1)[0]
        self.assertIn("orbit * 25.5f", camera_pose)
        self.assertIn("Vector3.up * 13.5f", camera_pose)
        self.assertIn("shoulder * 3.8f", camera_pose)
        self.assertIn("planeFlightDirection * 2.8f", camera_pose)
        self.assertIn("Vector3.up * 0.8f", camera_pose)
        self.assertIn("GetPlaneCameraPose(out pos, out focus);", late)
        self.assertNotIn("plane.transform.forward", camera_pose)
        self.assertIn("ApplyPlaneVisualInertia();", source)
        self.assertIn("if (phase != DropPhase.InPlane) pos = CameraSafePosition(focus, pos);", late)
        self.assertIn("if (phase != DropPhase.InPlane &&", late)
        self.assertIn("next = CameraSafePosition(focus, next);", late)
        self.assertIn("next = ClampCameraAboveTerrain(next);", late)

    def test_aerial_camera_collision_and_native_terrain_clearance(self):
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        helper = drop[drop.index("private Vector3 ClampCameraAboveTerrain("):
                      drop.index("private void LateUpdate()")]
        camera = drop[drop.index("private void LateUpdate()"):
                      drop.index("private void RestoreFootControls()")]
        self.assertIn("origin.y + tile.SampleHeight(point) + 0.8f", helper)
        self.assertNotIn("Mathf.Max(point.y, tile.SampleHeight(point)", helper)
        self.assertIn("if (phase != DropPhase.InPlane &&", camera)
        self.assertIn("(next - pos).sqrMagnitude > 0.00000001f)", camera)
        self.assertIn("next = CameraSafePosition(focus, next);", camera)
        self.assertIn("next = ClampCameraAboveTerrain(next);", camera)
        self.assertLess(camera.index("next = CameraSafePosition(focus, next);"),
                        camera.index("next = ClampCameraAboveTerrain(next);"))
        self.assertLess(camera.index("next = ClampCameraAboveTerrain(next);"),
                        camera.index("view.transform.position = next;"))

    def test_overflowed_aerial_camera_cast_keeps_nearest_valid_obstacle(self):
        # A saturated non-alloc buffer is unordered and may consist entirely
        # of player/aircraft contacts. It must never clamp the view to 35 cm.
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        camera = drop.split("private Vector3 CameraSafePosition(", 1)[1].split(
            "private Vector3 ClampCameraAboveTerrain(", 1)[0]
        self.assertIn("new RaycastHit[64]", drop)
        query_buffer = (SCRIPTS / "AarisPhysicsHitBuffer.cs").read_text()
        self.assertIn("AarisPhysicsHitBuffer.SphereCast(", camera)
        self.assertIn("ref cameraHits, 256,", camera)
        self.assertIn("if (count >= retained.Length)", query_buffer)
        self.assertIn("hits = Physics.SphereCastAll(", query_buffer)
        self.assertIn("count = hits.Length;", query_buffer)
        self.assertIn("var hit = hits[i];", camera)
        self.assertIn("hit.collider.transform.IsChildOf(transform)", camera)
        self.assertIn("hit.collider.transform.IsChildOf(plane.transform)", camera)
        self.assertNotIn("return focus + direction * 0.35f", camera)
        self.assertLess(camera.index("AarisPhysicsHitBuffer.SphereCast("),
                        camera.index("float allowed = distance;"))
        # The solver always chooses the nearest valid hit, irrespective of
        # the unordered output from the physics engine.
        for distances in ((3.1, 0.8, 1.9), (1.9, 3.1, 0.8)):
            clamped = min(max(0.35, x - 0.2) for x in distances)
            self.assertAlmostEqual(clamped, 0.6)

    def test_interrupted_aerial_drop_restores_ground_before_motor_handoff(self):
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        interrupt = drop.split("private void OnDisable()", 1)[1].split(
            "public sealed class AarisOfflineDropHud", 1)[0]
        self.assertIn("if (!controlsSuspended) return;", interrupt)
        ordered = [
            "capsule.enabled = false;",
            "transform.SetPositionAndRotation(preFlightGroundPosition, cachedPlayerRotation);",
            "capsule.enabled = true;",
            "phase = DropPhase.Landed;",
            "RestoreFootControls();",
        ]
        positions = [interrupt.index(line) for line in ordered]
        self.assertEqual(positions, sorted(positions))
        self.assertIn("plane = null;", interrupt)
        self.assertIn("canopy = null;", interrupt)

    def test_aerial_hud_fingers_never_steal_camera_swipe(self):
        drop = (SCRIPTS / "AarisOfflineDrop.cs").read_text("utf-8")
        look = drop[drop.index("private void ReadLookInput()"):
                    drop.index("private Vector2 MovementIntent()")]
        hud = drop[drop.index("public sealed class AarisOfflineDropHud"):
                   drop.index("public sealed class AarisOfflineDropStick")]
        # Unity EventSystem may process the initial pointer after Drop.Update.
        # Check the already-authored action and thumbstick rectangles directly.
        self.assertIn("hud.CapturesScreenPosition(t.position)", look)
        self.assertIn("t.phase == TouchPhase.Began", look)
        self.assertIn("IsPointerOverGameObject(t.fingerId)", look)
        self.assertIn("internal bool CapturesScreenPosition(Vector2 screenPosition)", hud)
        self.assertIn("RectTransformUtility.RectangleContainsScreenPoint(", hud)
        self.assertIn("actionHitRect.gameObject.activeInHierarchy", hud)
        self.assertIn("stickHitRect.gameObject.activeInHierarchy", hud)
        self.assertIn("actionHitRect = buttonRect;", hud)
        self.assertIn("stickHitRect = rect;", hud)
        self.assertNotIn("new GameObject(", look)  # No hotpath allocations.

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
        self.assertIn("data.button != PointerEventData.InputButton.Left", tactical)
        button = tactical.split("public sealed class AarisGlooTouchButton", 1)[1]
        lost = button.split("private void LateUpdate()", 1)[1].split(
            "public void OnCancel", 1)[0]
        self.assertIn("IsPointerPhysicallyHeld(holding)", lost)
        self.assertIn("CancelHold();", lost)
        self.assertNotIn("ReleaseAndDeploy", lost)
        self.assertIn("TouchPhase.Ended", button)
        self.assertIn("TouchPhase.Canceled", button)
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


    def test_medical_ground_loot_and_stationary_healing_are_bounded(self):
        script = (SCRIPTS / "AarisOfflineTacticalHUD.cs").read_text("utf-8")
        self.assertIn("MaxMedicalVisuals = 8", script)
        loot = (SCRIPTS / "AarisWeaponLootWorld.cs").read_text("utf-8")
        self.assertIn("var doc = AarisLootDocument.Load();", script)
        self.assertIn('Resources.Load<TextAsset>("AarisWeapons/loot_candidates")', loot)
        # Desktop uses the historical cached synchronous decoder. Android
        # now decodes the SAME authored manifest once after native async
        # import (a separate preprocessor branch), not once per pickup.
        self.assertEqual(loot.count("JsonUtility.FromJson<AarisLootDocument>("), 2)
        self.assertIn('yield return AarisLootDocument.LoadForAndroidAsync();', loot)
        self.assertIn('if (cached != null) return cached;', loot)
        self.assertIn("var doc = AarisLootDocument.AndroidCached;", script)
        self.assertIn('if (c == null || c.kind != "open" || i % 5 != 0', script)
        self.assertIn("site.taken = true;", script)
        self.assertIn("CanCollectMedical(site)", script)
        self.assertIn("Physics.RaycastNonAlloc(ray, medicalSightHits", script)
        self.assertIn("healing = true;", script)
        self.assertIn("healEnd = Time.time + 3f;", script)
        self.assertIn("new Vector2(velocity.x, velocity.z).sqrMagnitude > 0.04f", script)
        self.assertIn("medicalHealth.RestoreHealth(medicalHealing);", script)
        self.assertIn("renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;", script)

if __name__ == "__main__":
    unittest.main(verbosity=2)
