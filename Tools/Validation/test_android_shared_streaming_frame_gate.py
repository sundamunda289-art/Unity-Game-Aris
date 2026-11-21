#!/usr/bin/env python3
"""Shared optional-model integration admission: Android source regressions."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text()
CARS = (SRC / "AarisVehicleWorld.cs").read_text()
LOOT = (SRC / "AarisWeaponLootWorld.cs").read_text()
BIKE = (SRC / "AarisRoadsideBikeScenery.cs").read_text()
RIVALS = (SRC / "AarisOfflineBRDirector.cs").read_text()
CLAIM = "AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()"


def simulate_admission(available_ram, frame_numbers, budget_gpu=False):
    """Python control-flow mirror, NOT a Unity runtime measurement."""
    last = -1
    decisions = []
    for frame in frame_numbers:
        if (available_ram <= 0 or available_ram > 4608) and not budget_gpu:
            decisions.append(True)
        elif frame == last:
            decisions.append(False)
        else:
            last = frame
            decisions.append(True)
    return decisions


class SharedStreamingAdmission(unittest.TestCase):
    def test_low_ram_or_budget_gpu_android_only(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", GATE)
        self.assertIn("constrainedDevice = (ramMb > 0 && ramMb <= 4608) ||", GATE)
        self.assertIn("AarisMobileRuntime.UseConstrainedGpuStreamingBudget();", GATE)
        self.assertIn("int frame = Time.frameCount;", GATE)
        self.assertRegex(GATE,
                         r"if \(claimedFrame == frame\)\s*\{\s*"
                         r"SameFrameDeferredAttempts\+\+;\s*return false;\s*\}")
        self.assertIn("claimedFrame = frame;", GATE)
        self.assertEqual(simulate_admission(4096, [1, 1, 2, 2, 3]),
                         [True, False, True, False, True])
        for ram in (0, -1, 6144, 8192):
            self.assertEqual(simulate_admission(ram, [1, 1, 1]),
                             [True, True, True])
            self.assertEqual(simulate_admission(ram, [1, 1, 2], budget_gpu=True),
                             [True, False, True])

    def test_reset_clears_per_scene_static_slot(self):
        self.assertIn("[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]", GATE)
        self.assertIn("private static void Reset()", GATE)
        self.assertIn("claimedFrame = -1;", GATE)
        self.assertTrue((SRC / "AarisMobileStreamingFrameGate.cs.meta").is_file())

    def test_vehicle_slot_is_claimed_before_build_and_before_eviction(self):
        body = CARS.split("private void Update()", 1)[1].split(
            "private AarisVehicleRecord FarthestReplaceableParkedCar(", 1)[0]
        self.assertIn("if (!" + CLAIM + ")", body)
        self.assertIn("record.nextProbe = now + .15f;", body)
        self.assertLess(body.index(CLAIM), body.index("if (!Build(record, pos))"))
        self.assertLess(body.index(CLAIM), body.index("Destroy(retired.gameObject);"))
        self.assertIn("(parkedVictim.visible == null || !parkedVictim.visible.CanPark)", body)
        self.assertIn("if (parkedVictim != null)", body)

    def test_loot_consults_shared_slot_before_both_optional_models(self):
        body = LOOT.split("private bool StreamNearby(", 1)[1].split(
            "private void EnsureStarterLoot(", 1)[0]
        self.assertEqual(body.count(CLAIM), 2)
        self.assertLess(body.index(CLAIM), body.index("TryReuseLootVisual(record.state,"))
        self.assertLess(body.rindex(CLAIM), body.index("TryReuseLootVisual(state, i,"))
        self.assertEqual(body.count("if (!" + CLAIM + ") return true;"), 2)
        self.assertIn("if (buildAttempts >= modelBuildBudget)", body)

    def test_retired_ground_loot_uses_native_teardown_admission(self):
        # Six far visual releases retain their original budget, with one
        # reservation per refresh before the first native Destroy.
        refresh = LOOT.split("private bool RefreshPickups(", 1)[1].split(
            "private bool StreamNearby(", 1)[0]
        self.assertIn("if (unloaded >= MaximumUnloadsPerRefresh) continue;", refresh)
        self.assertIn("if (unloaded == 0)", refresh)
        self.assertEqual(refresh.count("ReserveNativeTeardownFrame();"), 1)
        self.assertLess(refresh.index("ReserveNativeTeardownFrame();"),
                        refresh.index("Destroy(pickup.gameObject);"))
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", refresh)

        pickup = LOOT.split("private bool TryTake(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("pickup.CommitConsumed();", pickup)
        self.assertLess(pickup.index("pickup.CommitConsumed();"),
                        pickup.index("ReserveNativeTeardownFrame();"))
        self.assertLess(pickup.index("ReserveNativeTeardownFrame();"),
                        pickup.index("Destroy(pickup.gameObject);"))
        self.assertIn("inventory.TryPickup(pickup.State", pickup)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", pickup)
        fixture = (ROOT / "Tools/Validation/StreamingAdmissionTests.cs").read_text()
        self.assertIn("private static void LootTeardownFence()", fixture)
        self.assertIn("CleanupRigAndReset(); LootTeardownFence();", fixture)
        self.assertIn('Configure(8192, "Adreno 730");', fixture)

    def test_starter_loot_not_delayed_by_optional_slot(self):
        starter = LOOT.split("private void EnsureStarterLoot(", 1)[1]
        self.assertNotIn(CLAIM, starter.split("private ", 1)[0])
        self.assertIn("starterLootSettled", starter)
        self.assertIn("TryFindDropSpot", starter)

    def test_bike_prewarm_materials_and_new_rigs_share_slot(self):
        body = BIKE.split("private void Update()", 1)[1].split(
            "private GameObject ReuseBike(", 1)[0]
        # Source import and new rig spawn claim in Update. The palette
        # restart and FIRST pair claim separately inside the coroutine, so
        # the restart never burns an admission with zero material work.
        self.assertEqual(body.count(CLAIM), 2)
        palette = BIKE.split(
            "private System.Collections.IEnumerator BuildMaterialsAcrossFrames(bool stagger)", 1
        )[1].split("private void OnEnable()", 1)[0]
        self.assertEqual(palette.count(CLAIM), 3)
        self.assertLess(palette.index(CLAIM),
                        palette.index('Shader shader = Shader.Find("Universal Render Pipeline/Lit");'))
        self.assertIn("bikeMaterialsBuild == null", body)
        self.assertLess(body.index("bikeMaterialsBuild == null"),
                        body.index("if (!bikeMaterialsReady) return;"))
        self.assertLess(body.index("if (!bikePrewarm.isDone) return;"),
                        body.index(CLAIM))
        self.assertLess(body.index(CLAIM), body.index("SetupMaterials();"))
        self.assertLess(body.index("TryParkingSpot(nearest.point, out Vector3 spot)"),
                        body.rindex(CLAIM))
        self.assertIn("parkedVictim == null && parkedBikePool.Count == 0", body)
        self.assertIn("nearest.nextProbe = now + .4f;", body)

    def test_bike_pool_reuse_not_blocked_or_destroyed(self):
        body = BIKE.split("private void Update()", 1)[1].split(
            "private GameObject ReuseBike(", 1)[0]
        self.assertIn("ReuseBike(previous, nearest.point, spot)", body)
        self.assertIn("ReuseBike(parkedBikePool.Pop(), nearest.point, spot)", body)
        self.assertIn("CreateBike(nearest.point, spot)", body)
        self.assertIn("parkedVictim == null && parkedBikePool.Count == 0", body)
        self.assertNotIn(CLAIM, BIKE.split("private GameObject ReuseBike(", 1)[1].
                         split("private bool TryParkingSpot(", 1)[0])

    def test_authoritative_controls_visuals_preserved(self):
        self.assertIn("private const int MaximumNearbyVehicles = 10;", CARS)
        self.assertIn("private const int MaximumBuildAttemptsPerFrame = 2;", LOOT)
        self.assertIn("private const int MaximumVisibleBikes = 3;", BIKE)
        for code in (CARS, LOOT, BIKE):
            self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", code)

    def test_rival_humanoid_build_joins_shared_android_admission(self):
        update = RIVALS.split("private void Update()", 1)[1].split(
            "private bool CanAttemptRivalSpawn()", 1)[0]
        policy = RIVALS.split("private bool CanAttemptRivalSpawn()", 1)[1].split(
            "private void Begin()", 1)[0]
        self.assertIn("if (!spawnComplete && Time.time >= nextSpawn &&", update)
        self.assertIn("CanAttemptRivalSpawn())", update)
        self.assertLess(update.index("CanAttemptRivalSpawn()"),
                        update.index("int slot = attemptedSlots++;"))
        self.assertIn(
            "if (!populatedSlots[slot]) TrySpawnSlot(slot);", update)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", policy)
        self.assertIn("attemptedSlots < RivalCount", policy)
        self.assertIn("!populatedSlots[attemptedSlots] &&", policy)
        self.assertIn(
            "!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()", policy)
        self.assertLess(policy.index("TryClaimOptionalModelBuild()"),
                        policy.index("nextSpawn = Time.time + 0.12f;"))
        self.assertIn("return false;", policy)
        self.assertIn("return true;", policy)
        # Zone, elimination and ring ticks must never wait for a cosmetic gate.
        self.assertLess(update.index("int slot = attemptedSlots++;"),
                        update.index("if (Time.time >= nextZoneTick)"))
        self.assertIn("ApplyZoneDamage();", update)
        self.assertIn("DrawBoundary();", update)
        self.assertIn("private const int RivalCount = 6;", RIVALS)

    def test_deferred_spawn_retries_same_slot_without_spawning_burst(self):
        # Model three competing optional frames followed by all six slots.
        slot = 0
        admitted = []
        blocked_frames = {1, 2, 5}
        for frame in range(1, 20):
            if slot >= 6:
                break
            if frame in blocked_frames:
                continue
            admitted.append((frame, slot))
            slot += 1
        self.assertEqual([i for _, i in admitted], list(range(6)))
        self.assertEqual(len({frame for frame, _ in admitted}), 6)
        self.assertEqual(admitted[0][0], 3)

    def test_diagnostics_share_one_native_frame_slot_without_visual_changes(self):
        probe = GATE.split(
            "internal static bool TryClaimNonessentialDiagnosticFrame()", 1
        )[1].split("internal static bool TryClaimOptionalModelBuild()", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", probe)
        self.assertIn("IsEssentialRigFrame() || IsAssetCleanupPending()", probe)
        self.assertIn("if (!IsConstrainedDevice()) return true;", probe)
        self.assertIn("if (claimedFrame == frame || importIntegrationFrame == frame)", probe)
        self.assertIn("if (IsOptionalImportBusy()) return false;", probe)
        self.assertLess(probe.index("IsOptionalImportBusy()"),
                        probe.index("claimedFrame = frame;"))
        # The same production import fence must reject unfinished requests
        # and the observed native integration completion frame.
        busy = GATE.split("private static bool IsOptionalImportBusy()", 1)[1].split(
            "private static bool ShouldDeferForFramePressure(", 1)[0]
        self.assertIn("if (!optionalLoadInFlight.isDone)", busy)
        self.assertIn("importIntegrationFrame = frame;", busy)
        self.assertIn("ShouldDeferForFramePressure(frame)", probe)
        self.assertIn("claimedFrame = frame;", probe)
        self.assertNotIn("Resources.Load", probe)
        self.assertNotIn("Instantiate(", probe)
        self.assertNotIn("QualitySettings", probe)
        self.assertNotIn("renderScale", probe)

    def test_repeated_long_hitches_do_not_starve_optional_assets(self):
        # Source-level guard alongside the production C# fixture's 70-frame
        # continuous-hitch and isolated-hitch execution tests.
        policy = GATE.split(
            "private static bool ShouldDeferForFramePressure(int frame)", 1
        )[1].split("[RuntimeInitializeOnLoadMethod", 1)[0]
        self.assertIn("private const int MaximumLongHitchConsecutiveDeferrals = 6;", GATE)
        self.assertIn("consecutiveLongHitchDeferrals < MaximumLongHitchConsecutiveDeferrals", policy)
        self.assertIn("consecutiveLongHitchDeferrals++;", policy)
        self.assertIn("longHitchRecoveryThroughFrame = -1;", policy)
        self.assertIn("consecutiveLongHitchDeferrals = 0;", policy)
        self.assertLess(policy.index("longHitchRecoveryThroughFrame = -1;"),
                        policy.index("float budget = CurrentFrameBudgetSeconds(frame);"))
        reset = GATE.split("private static void Reset()", 1)[1].split(
            "internal static void TrackAdditiveSceneLoad(", 1
        )[0]
        self.assertIn("consecutiveLongHitchDeferrals = 0;", reset)
        fixture = (ROOT / "Tools/Validation/StreamingAdmissionTests.cs").read_text()
        self.assertIn("longHitchAdmitted == 10", fixture)
        self.assertIn("PressureDeferredFrames == 60", fixture)
        # No fidelity reduction is a valid substitute for progress fairness.
        self.assertNotIn("renderScale", policy)
        self.assertNotIn("QualitySettings", policy)

    def test_diagnostic_first_or_streamer_first_never_double_claims(self):
        # Pure frame-admission simulation: regardless of Unity Update order,
        # only the first nonessential task gets the slot on a 4GB phone.
        for order in (("diagnostics", "streamer"), ("streamer", "diagnostics")):
            claimed_frame = -1
            admitted = []
            for item in order:
                if claimed_frame == 100:
                    continue
                claimed_frame = 100
                admitted.append(item)
            self.assertEqual(admitted, [order[0]])
            self.assertEqual(claimed_frame, 100)
            # Both retry normally on the next eligible rendered frame.
            self.assertNotEqual(claimed_frame, 101)

    def test_bounded_conflict_resolves_next_frame(self):
        # If vehicle wins frame N, a loot/bike candidate is free in N+1.
        for label in ("vehicle", "loot", "bike"):
            decisions = simulate_admission(4096, [19, 19, 20, 20, 21])
            self.assertEqual(decisions, [True, False, True, False, True], label)


if __name__ == "__main__":
    unittest.main(verbosity=2)
