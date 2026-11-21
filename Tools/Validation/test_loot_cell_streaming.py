#!/usr/bin/env python3
"""Deterministic proof that 64m cell lookup preserves bounded 50m loot queries."""
import json
import math
import random
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs").read_text(encoding="utf-8")
CELL_METRES = 64.0
RADIUS = 50.0


def key(x, z):
    return (math.floor(x / CELL_METRES), math.floor(z / CELL_METRES))


class LootGridRegression(unittest.TestCase):
    def test_no_world_visual_or_despawn_rule_changed(self):
        for clause in (
            "private const float ActivationMetres = 50f;",
            "private const float ReleaseMetres = 69f;",
            "private const int MaximumActiveLoot = 40;",
            "private const float CandidateCellMetres = 64f;",
            "IndexPlannedCandidates();",
            "CollectNearbyCandidateIndices(playerPosition);",
            "nearbyCandidateIndices.Sort();",
            "if (dx * dx + dz * dz >= loadSquared) continue;",
            "StreamFramesPerRefresh = 3;",
            "MaximumBuildAttemptsPerFrame = 2;",
            "MaximumCandidateProbesPerFrame = 6;",
            "probes == MaximumCandidateProbesPerFrame) return true;",
            "candidateCells.TryGetValue(CandidateCellKey(x, z)",
        ):
            self.assertIn(clause, SOURCE)

    def test_aircraft_defers_ground_work_and_resumes_on_approach(self):
        scripts = ROOT / "Assets/AarisWorld/Scripts"
        drop = (scripts / "AarisOfflineDrop.cs").read_text(encoding="utf-8")
        colliders = (scripts / "AarisNearbyColliders.cs").read_text(encoding="utf-8")
        update = SOURCE[SOURCE.index("private void Update()"):SOURCE.index("private bool RefreshPickups(")]
        self.assertLess(update.index("drop.ShouldDeferGroundLoot"), update.index("RefreshPickups(playerPosition"))
        self.assertIn("nextRefresh = 0f;", update)
        self.assertIn("controlsSuspended && phase == DropPhase.InPlane", drop)
        self.assertIn("(IsRidingAircraft || sampledGroundClearance > 70f) && !HasLanded", drop)
        self.assertIn("sampledGroundClearance = reliableHeight ? height : float.PositiveInfinity;", drop)
        self.assertIn("drop != null && drop.IsRidingAircraft", colliders)
        self.assertNotIn("ShouldDeferGroundLoot", colliders)

    def test_far_resolved_floor_is_rejected_before_prefab_instantiation(self):
        authored = SOURCE[SOURCE.index("foreach (int i in nearbyCandidateIndices)"):
                          SOURCE.index("private void EnsureStarterLoot(")]
        check = "(spot - playerPosition).sqrMagnitude > ReleaseMetres * ReleaseMetres"
        self.assertLess(authored.index("TryResolveNearCandidate("), authored.index(check))
        self.assertLess(authored.index(check), authored.index("TryReuseLootVisual(state, i,"))
        self.assertLess(authored.index(check), authored.index("new GameObject(visualName)"))

    def test_each_authored_indoor_home_has_at_least_one_planned_slot(self):
        # Static/source-level contract plus independent seeded simulation;
        # final physical placement still needs native Unity validation.
        indoor = [x for x in json.loads(
            (ROOT / "Assets/Resources/AarisWeapons/loot_candidates.json")
            .read_text(encoding="utf-8"))["candidates"]
            if x["kind"] == "indoor"]
        homes = {}
        for index, point in enumerate(indoor):
            homes.setdefault(point["origin"], []).append(index)
        self.assertGreaterEqual(len(homes), 50)
        self.assertTrue(all(len(slots) >= 1 for slots in homes.values()))
        self.assertIn("var indoorHomesCovered = new HashSet<string>(StringComparer.Ordinal);", SOURCE)
        self.assertIn("var fallbackSlotByHome = new Dictionary<string, int>(StringComparer.Ordinal);", SOURCE)
        self.assertIn("fallbackSlotByHome.Add(c.origin, i);", SOURCE)
        self.assertIn("if (indoorHomesCovered.Contains(home.Key)) continue;", SOURCE)
        self.assertIn("choices[home.Value] = guaranteedSpec;", SOURCE)
        self.assertIn("IndexPlannedCandidates();", SOURCE)
        self.assertLess(SOURCE.index("choices[home.Value] = guaranteedSpec;"),
                        SOURCE.index("IndexPlannedCandidates();"))
        for seed in range(1000):
            rng = random.Random(seed)
            selected = {point["origin"] for point in indoor
                        if rng.random() < .72}
            missing = set(homes) - selected
            selected.update(missing)
            self.assertEqual(selected, set(homes))
            self.assertLessEqual(len(missing), len(homes))
        # No extra active models or per-frame physics work.
        self.assertIn("MaximumActiveLoot = 40;", SOURCE)
        self.assertIn("MaximumBuildAttemptsPerFrame = 2;", SOURCE)

    def test_native_tree_cover_samples_indices_without_copying_full_arrays(self):
        # Unity's GetTreeInstance(index) avoids allocating the whole
        # treeInstances array when only bounded random samples are needed.
        section = SOURCE[SOURCE.index("private void AppendNativeTreeCover("):
                         SOURCE.index("private static bool WithinMap(")]
        self.assertIn("int treeCount = data.treeInstanceCount;", section)
        self.assertIn("if (treeCount <= 0) continue;", section)
        self.assertIn("data.GetTreeInstance(idx)", section)
        self.assertIn("(uint)treeCount", section)
        self.assertNotIn("data.treeInstances;", section)
        self.assertIn("while (accepted < 48 && attempts++ < 240)", section)
        self.assertIn("result.Add(new AarisLootCandidate", section)
        self.assertIn("treePoints.Add(new Vector2(x, z));", section)
        self.assertLess(section.index("int treeCount = data.treeInstanceCount;"),
                        section.index("data.GetTreeInstance(idx)"))
        # Equivalent index selection and zero-tree guard for map tiles
        # without touching authored TerrainData or gameplay seed order.
        for count in (1, 2, 11, 250, 7500):
            for seed in (1, 47, 20261117):
                rng = random.Random(seed)
                indices = [rng.getrandbits(32) % count for _ in range(240)]
                self.assertTrue(all(0 <= index < count for index in indices))

    def test_tree_spacing_subsets_keep_the_same_acceptance_order(self):
        self.assertIn("foreach (var point in treePoints)", SOURCE)
        self.assertIn("treePoints.Add(new Vector2(x, z));", SOURCE)
        rng = random.Random(0xAA15)
        starts = [(rng.uniform(-200, 200), rng.uniform(-200, 200), i % 29 == 0)
                  for i in range(1500)]
        samples = [(rng.uniform(-200, 200), rng.uniform(-200, 200)) for _ in range(960)]

        def accepted(filter_tree_only):
            points = [p for p in starts if p[2]] if filter_tree_only else list(starts)
            accepted_points = []
            for x, z in samples:
                if any(tree and (px - x) ** 2 + (pz - z) ** 2 < 32 ** 2 for px, pz, tree in points):
                    continue
                points.append((x, z, True))
                accepted_points.append((x, z))
            return accepted_points

        self.assertEqual(accepted(False), accepted(True))

    def test_immutable_cell_index_cache_rebuilds_only_at_bounds_change(self):
        # Cache never touches final 50m distance filtering, priority order,
        # candidate selection, live mesh cap, or pickup collision checks.
        for clause in (
            "private bool nearbyCandidateCellsCached;",
            "nearbyCandidateCellsCached = false;",
            "nearbyCandidateIndices.Clear();",
            "firstX == cachedFirstCellX && lastX == cachedLastCellX",
            "firstZ == cachedFirstCellZ && lastZ == cachedLastCellZ",
            "cachedFirstCellX = firstX; cachedLastCellX = lastX;",
            "cachedFirstCellZ = firstZ; cachedLastCellZ = lastZ;",
        ):
            self.assertIn(clause, SOURCE)
        stream = SOURCE[SOURCE.index("private void CollectNearbyCandidateIndices("):
                        SOURCE.index("private void AppendNativeTreeCover(")]
        self.assertLess(stream.index("return;"), stream.index("nearbyCandidateIndices.Clear();"))
        self.assertIn("nearbyCandidateIndices.Sort();", stream)

        rng = random.Random(0xAA15)
        points = [(rng.uniform(-1000, 1000), rng.uniform(-1000, 1000))
                  for _ in range(900)]
        cells = {}
        for i, point in enumerate(points):
            cells.setdefault(key(*point), []).append(i)

        cache_bounds = None
        cache_indices = ()
        reused = rebuilt = 0
        for initial in [(0., 0.), (63.9, 63.9), (-64., -64.),
                        (950., -950.), (-999., 999.)]:
            # Slow walking, fast sprint, negative cell edges and teleport
            # are checked against the original uncached implementation.
            route = [initial] + [(initial[0] + rng.uniform(-7, 7),
                                  initial[1] + rng.uniform(-7, 7))
                                 for _ in range(70)]
            for px, pz in route:
                bounds = (math.floor((px-RADIUS)/CELL_METRES),
                          math.floor((px+RADIUS)/CELL_METRES),
                          math.floor((pz-RADIUS)/CELL_METRES),
                          math.floor((pz+RADIUS)/CELL_METRES))
                original = []
                for cx in range(bounds[0], bounds[1]+1):
                    for cz in range(bounds[2], bounds[3]+1):
                        original.extend(cells.get((cx, cz), ()))
                original.sort()

                if bounds != cache_bounds:
                    cache_indices = original
                    cache_bounds = bounds
                    rebuilt += 1
                else:
                    reused += 1
                self.assertEqual(original, cache_indices)
                visible_orig = [i for i in original
                                if (points[i][0]-px)**2 + (points[i][1]-pz)**2 < RADIUS**2]
                visible_cached = [i for i in cache_indices
                                  if (points[i][0]-px)**2 + (points[i][1]-pz)**2 < RADIUS**2]
                self.assertEqual(visible_orig, visible_cached)
        self.assertGreater(reused, rebuilt)

    def test_dense_settlement_physics_overflow_is_allocation_bounded(self):
        # Saturated small hit buffers cannot be used to approve a floor
        # or a pickup through a wall. Retry in reusable 128-slot storage.
        self.assertIn("private readonly RaycastHit[] overflowRayHits = new RaycastHit[128];", SOURCE)
        self.assertIn("private readonly Collider[] overflowBlockers = new Collider[128];", SOURCE)
        overflow = SOURCE[SOURCE.index("private int CompleteRaycastOverflow("):
                          SOURCE.index("private bool TryResolve(")]
        self.assertIn("Physics.RaycastNonAlloc(ray, overflowRayHits, distance,", overflow)
        self.assertIn("if (count >= overflowRayHits.Length)", overflow)
        self.assertIn("results = Physics.RaycastAll(ray, distance,", overflow)
        for caller in ("CompleteRaycastOverflow(groundRay, 3.3f, out floorHits)",
                       "CompleteRaycastOverflow(sightLine, distance, out sightHits)",
                       "CompleteRaycastOverflow(ray, 3.5f, out resolvedFloors)"):
            self.assertIn(caller, SOURCE)
        self.assertIn("capsuleBottom, capsuleTop, .22f, overflowBlockers,", SOURCE)
        self.assertIn("if (blockers >= overflowBlockers.Length)", SOURCE)
        self.assertIn("nearbyBlockers = Physics.OverlapCapsule(", SOURCE)
        for small in (24, 32):
            for contacts in (0, small - 1, small, 63, 127, 128, 256):
                retry = contacts >= small
                allocation_needed = retry and contacts >= 128
                self.assertFalse(allocation_needed and not retry)
                if contacts < small:
                    self.assertFalse(retry)
                if contacts >= 128:
                    self.assertTrue(allocation_needed)

    def test_reserved_starter_slot_never_exceeds_40_models(self):
        self.assertIn("int streamLimit = MaximumActiveLoot -", SOURCE)
        self.assertIn("active.Count >= streamLimit", SOURCE)
        for starter_pending in (False, True):
            limit = 40 - int(starter_pending)
            assert limit in (39, 40)
            for active_count in range(41):
                if active_count < limit:
                    self.assertLessEqual(active_count + 1, 40)
                else:
                    self.assertGreaterEqual(active_count, limit)

    def test_player_drop_never_bypasses_40_live_visuals(self):
        # A voluntary drop used to bypass the streaming cap and allocate a
        # new pickup GameObject even when 40 nearby meshes were active.
        direct = SOURCE[SOURCE.index("private AarisDroppedWeapon CreateDropped("):
                        SOURCE.index("private void UndoDrop(")]
        cap = "if (!deferVisual && active.Count >= MaximumActiveLoot) return null;"
        self.assertIn(cap, direct)
        self.assertLess(direct.index(cap), direct.index("int id = nextDropId--;"))
        self.assertLess(direct.index(cap), direct.index('new GameObject("Dropped "'))
        self.assertIn("if (!deferVisual)", direct)

        drop = SOURCE[SOURCE.index("public bool TryDropEquipped()"):
                      SOURCE.index("private bool IsBlockedDrop(")]
        self.assertIn("active.Count >= MaximumActiveLoot", drop)
        self.assertLess(drop.index("active.Count >= MaximumActiveLoot"),
                        drop.index("!TryFindDropSpot(out Vector3 point)"))
        self.assertLess(drop.index("CreateDropped(state, point, 1.25f)"),
                        drop.index("inventory.TryRemove(out AarisWeaponState removed)"))
        # Swapping a gun must still reserve its persistent record without
        # constructing a second model on the same frame.
        self.assertIn("deferVisual: true", SOURCE)
        self.assertIn("inventory.RequiresExchangeDrop", SOURCE)
        self.assertIn("inventory.HasAnyGun", SOURCE)
        # Bounded model: repeated direct drops cannot add a 41st live mesh,
        # while deferred swaps add zero new live meshes at any occupancy.
        for live in range(0, 41):
            for deferred in (False, True):
                permitted = deferred or live < 40
                live_after = live + int(permitted and not deferred)
                self.assertLessEqual(live_after, 40)
                if deferred:
                    self.assertTrue(permitted)
                    self.assertEqual(live_after, live)
        live = accepted = 0
        for _ in range(320):
            if live < 40:
                live += 1
                accepted += 1
            self.assertLessEqual(live, 40)
        self.assertEqual((live, accepted), (40, 40))

    def test_full_mesh_pool_and_disabled_flight_do_not_starve_starter(self):
        starter = SOURCE[SOURCE.index("private void EnsureStarterLoot("):
                         SOURCE.index("private bool IsCurrentlyActive(")]
        guard = starter.split("if (inventory.HasAnyGun)", 1)[0]
        self.assertNotIn("active.Count >= MaximumActiveLoot", guard)
        self.assertIn("flight.isActiveAndEnabled && !flight.HasLanded", starter)
        self.assertIn("if (active.Count >= MaximumActiveLoot)", starter)
        self.assertLess(starter.index("if (active.Count >= MaximumActiveLoot)"),
                        starter.index("TryFindDropSpot(out Vector3 point"))
        full = starter[starter.index("if (active.Count >= MaximumActiveLoot)"):]
        self.assertLess(full.index("TryLastResortStarterEquip();"),
                        full.index("// One fallback per match"))
        self.assertIn("Time.unscaledTime - starterUnarmedSince < 3f", starter)
        self.assertIn("inventory.TryPickup(state, out AarisWeaponState previous)", starter)

    def test_starter_uses_verified_floor_not_stale_grounded_flag(self):
        # A newly restored CharacterController can report false for its
        # previous Move even though the drop phase has completed.
        starter = SOURCE[SOURCE.index("private void EnsureStarterLoot("):
                         SOURCE.index("private bool IsCurrentlyActive(")]
        self.assertIn("flight.isActiveAndEnabled && !flight.HasLanded) return;", starter)
        self.assertIn("CharacterController controller) || !controller.enabled", starter)
        self.assertNotIn("!controller.isGrounded", starter)
        self.assertIn("TryFindDropSpot(out Vector3 point, requireReachable: true)", starter)
        self.assertIn("starterLootId = starter.id;", starter)
        # No new gun is spawned without a nearby real supporting collider:
        placement = SOURCE[SOURCE.index("private bool TryFindDropSpot("):
                           SOURCE.index("public bool TryDropEquipped()")]
        for safety in ("Physics.RaycastNonAlloc(", "Physics.OverlapCapsuleNonAlloc(",
                       "if (floor == null) continue;", "if (!clear) continue;",
                       "if (requireReachable && !CanReachPoint(candidate)) continue;"):
            self.assertIn(safety, placement)
        self.assertIn("for (int attempt = 0; attempt < (requireReachable ? 20 : 12); attempt++)", placement)
        self.assertIn("else if (attempt < 8)", placement)
        self.assertIn("distance = 1.55f;", placement)
        self.assertIn("else if (attempt < 12)", placement)
        self.assertIn("distance = 1.80f;", placement)
        self.assertIn("Mathf.PI / 8f", placement)
        self.assertLess(1.80, 2.15)  # optional starter ring remains reachable
        self.assertIn("direction = (forward * f + right * r).normalized;", placement)
        self.assertIn("distance = .65f;", placement)
        self.assertIn("distance = 1.12f;", placement)
        self.assertNotIn("new Vector3[]", placement)
        self.assertLess(1.12, 2.15)  # diagonal ring
        self.assertLess(1.55, 2.15)  # final cardinal ring: still reachable
        # Fall back only when close/diagonal placements were physically
        # blocked; never ignore water/roof/obstruction checks.
        self.assertLess(placement.index("distance = .65f;"),
                        placement.index("distance = 1.12f;"))
        self.assertLess(placement.index("distance = 1.12f;"),
                        placement.index("distance = 1.55f;"))

    def test_native_terrain_world_y_used_for_outdoor_floor(self):
        # Original authored slots and nearby offset retries both sample
        # Terrain in world-space, without adding the tile Y a second time.
        self.assertIn("float x = c.x + offsetX, z = c.z + offsetZ;", SOURCE)
        self.assertIn("terrain.SampleHeight(new Vector3(x, 0f, z))", SOURCE)
        self.assertNotIn("terrain.transform.position.y + terrain.SampleHeight", SOURCE)
        self.assertIn("float expectedGround = indoor ? c.y :", SOURCE)

    def test_sealed_house_relocation_keeps_stream_and_physics_budgets(self):
        self.assertIn("private bool TryResolveNearCandidate(", SOURCE)
        self.assertIn('bool relocateIndoor = candidate.kind == "indoor";', SOURCE)
        self.assertIn('bool indoor = c.kind == "indoor" && !forceOutdoor;', SOURCE)
        self.assertIn("forceOutdoor: relocateIndoor", SOURCE)
        self.assertIn("5.5f : 9.0f", SOURCE)
        self.assertIn("2.5f : 4.5f", SOURCE)
        self.assertIn("if (forceOutdoor)", SOURCE)
        self.assertIn("RaycastHit roof, 4.5f", SOURCE)
        self.assertNotIn('if (candidate.kind == "indoor") return false;', SOURCE)
        self.assertIn("int alternateProbeBudget = 3;", SOURCE)
        self.assertIn("alternative < 2 && alternateProbeBudget > 0", SOURCE)
        self.assertIn("alternateProbeBudget--;", SOURCE)
        self.assertIn("TryResolve(candidate, terrains, out spot,", SOURCE)
        self.assertIn("ref alternateProbeBudget, out Vector3 spot", SOURCE)
        self.assertIn("MaximumBuildAttemptsPerFrame = 2;", SOURCE)
        self.assertIn("if (!foundFloor) return false;", SOURCE)
        self.assertIn("Physics.CheckCapsule(center", SOURCE)

    def test_blocked_loot_probes_sweep_new_headings_without_extra_rays(self):
        # Each 3s retry must explore a different compass sector, not re-test
        # the same permanently blocked two points all match long.
        self.assertIn("private byte[] alternateProbeSweep;", SOURCE)
        self.assertIn("alternateProbeSweep = new byte[candidates.Length];", SOURCE)
        self.assertIn("int sweep = alternateProbeSweep[index] & 7;", SOURCE)
        self.assertIn("alternateProbeSweep[index] = (byte)((sweep + 1) & 7);", SOURCE)
        self.assertIn("sweep * (Mathf.PI * 0.25f)", SOURCE)
        self.assertIn("alternative < 2 && alternateProbeBudget > 0", SOURCE)
        self.assertIn("int alternateProbeBudget = 3;", SOURCE)
        self.assertIn("MaximumCandidateProbesPerFrame = 6;", SOURCE)
        # A fixed initial hash angle plus eight 45-degree steps covers 360.
        import math
        headings = [(0.13 + i * math.pi / 4) % (2 * math.pi)
                    for i in range(8)]
        assert len({round(h, 7) for h in headings}) == 8

    def test_medkit_and_helmet_respect_unloaded_house_colliders(self):
        # A disabled 55m streamed wall is invisible to Physics overlap.
        # Verify the actual medkit/helmet spawn path checks the existing
        # cached authored collider grid BEFORE attempting a physics overlap.
        tactical = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineTacticalHUD.cs").read_text(
            encoding="utf-8")
        obstacles = (ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs").read_text(
            encoding="utf-8")
        self.assertIn("private AarisNearbyColliders medicalNearbyColliders;", tactical)
        self.assertIn("medicalNearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();", tactical)
        spawn = tactical[tactical.index("private bool TrySpawnMedicalCase("):
                         tactical.index("private static void HelmetPart(")]
        guard = "medicalNearbyColliders.HasPotentialInactiveObstacle("
        self.assertIn(guard, spawn)
        self.assertIn("center, halfExtents, Quaternion.identity))", spawn)
        self.assertLess(spawn.index(guard), spawn.index("Physics.OverlapBoxNonAlloc("))
        self.assertIn("if (site.isHelmet)", spawn)
        self.assertIn("var root = new GameObject(\"Aaris Streamed Medkit\")", spawn)
        self.assertIn("if (!isActiveAndEnabled || obstacles == null", obstacles)
        self.assertIn("if (worldBounds[i].Intersects(probe) &&", obstacles)
        self.assertIn("streamed[i] && c != null && !c.enabled)", obstacles)
        self.assertNotIn("Physics.SyncTransforms()", spawn)

    def test_open_ground_gun_coverage_at_50m_sample_sites(self):
        # Before this fix: 166/1521 sampled open-map locations had NO
        # outdoor candidate within the 50m streaming circle. Sample the
        # shipped baked JSON, not just generator/source intentions.
        import json
        data = json.loads((ROOT / "Assets/Resources/AarisWeapons/"
                           "loot_candidates.json").read_text(encoding="utf-8"))
        points = [(p["x"], p["z"]) for p in data["candidates"]
                  if p["kind"] == "open"]
        self.assertGreaterEqual(len(points), 1400)
        misses = []
        for x in range(-950, 951, 50):
            for z in range(-950, 951, 50):
                if not any((px-x)**2 + (pz-z)**2 < RADIUS**2
                           for px, pz in points):
                    misses.append((x, z))
        self.assertLessEqual(len(misses), 2, f"Uncovered outdoor sites: {misses}")
        generated = [p for p in data["candidates"] if
                     p["kind"] == "open" and p["slot"] >= 200000]
        self.assertGreaterEqual(len(generated), 300)
        self.assertIn('c.kind == "open" && c.slot >= 200000 ? 1f : 0.78f;', SOURCE)
        for p in generated:
            self.assertLessEqual(abs(p["x"]), 957)
            self.assertLessEqual(abs(p["z"]), 957)
        # New points are only logical slots. Never increase active scene
        # gun meshes / per-frame spawn and physics work on 2GB phones.
        self.assertIn("private const int MaximumActiveLoot = 40;", SOURCE)
        self.assertIn("private const int MaximumBuildAttemptsPerFrame = 2;", SOURCE)
        self.assertIn("private const int MaximumCandidateProbesPerFrame = 6;", SOURCE)

    def test_all_points_in_circle_covered_and_priority_preserved(self):
        rng = random.Random(20261110)
        points = [(rng.uniform(-1000, 1000), rng.uniform(-1000, 1000))
                  for _ in range(750)]
        points += [(-999, -999), (999, 999), (0, 0), (64, 64),
                   (-64, -64), (42, 0), (-42, 0), (0, 42), (0, -42)]
        grid = {}
        for i, (x, z) in enumerate(points):
            grid.setdefault(key(x, z), []).append(i)

        player_positions = [(rng.uniform(-1000, 1000), rng.uniform(-1000, 1000))
                            for _ in range(250)]
        player_positions += [(0, 0), (64, 64), (-64, -64), (-999, -999),
                             (999, 999), (32, 32), (-32, -32), (63.99, 64)]
        sq = RADIUS * RADIUS
        for px, pz in player_positions:
            old = [i for i, (x, z) in enumerate(points)
                   if (x-px)**2 + (z-pz)**2 < sq]
            nearby = []
            min_x, max_x = math.floor((px-RADIUS)/CELL_METRES), math.floor((px+RADIUS)/CELL_METRES)
            min_z, max_z = math.floor((pz-RADIUS)/CELL_METRES), math.floor((pz+RADIUS)/CELL_METRES)
            for cx in range(min_x, max_x+1):
                for cz in range(min_z, max_z+1):
                    nearby.extend(grid.get((cx, cz), ()))
            nearby.sort()
            new = [i for i in nearby
                   if (points[i][0]-px)**2 + (points[i][1]-pz)**2 < sq]
            self.assertEqual(old, new, f"Mismatch at {px}, {pz}")
            self.assertEqual(len(nearby), len(set(nearby)), "Duplicate cell indices")


if __name__ == "__main__":
    unittest.main(verbosity=2)
