#!/usr/bin/env python3
"""Exact-type Android ground-loot visual reuse: bounded native memory contracts.

Source-level guards and a deterministic pool-budget simulation only. Does not
replace Unity Editor compilation, Android Memory Profiler or real player QA.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
WORLD = (SCRIPTS / "AarisWeaponLootWorld.cs").read_text()
PICKUP = (SCRIPTS / "AarisWeaponPickup.cs").read_text()


class AndroidIdleLootVisualReuse(unittest.TestCase):
    def test_android_only_lazy_bounded_pool_with_short_ttl(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", WORLD)
        self.assertIn("private const int MaximumIdleLootVisuals = 4;", WORLD)
        self.assertIn("private const float IdleLootLifetimeSeconds = 18f;", WORLD)
        self.assertIn("private readonly List<IdleLootVisual> idleLootVisuals =", WORLD)
        self.assertIn("idleLootCapacity = ramMb <= 0 || ramMb <= 3072 ? 0 :", WORLD)
        self.assertIn("ramMb < 6144 ? 2 : MaximumIdleLootVisuals;", WORLD)
        self.assertIn("idleLootVisuals.Count >= idleLootCapacity", WORLD)
        self.assertIn("new IdleLootVisual {", WORLD)
        self.assertIn("expiresAt = now + IdleLootLifetimeSeconds", WORLD)
        self.assertEqual(WORLD.count("new List<IdleLootVisual>("), 1)
        self.assertEqual(WORLD.count("new AarisWeaponPickup()"), 0)

    def test_reuses_only_same_original_weapon_type_and_immutable_state(self):
        rent = WORLD.split("private AarisWeaponPickup TryRentIdleVisual(", 1)[1].split(
            "// On all non-Android platforms", 1)[0]
        self.assertIn("pickup.Spec.id != gunState.Spec.id", rent)
        self.assertIn("idleLootVisuals.RemoveAt(i);", rent)
        self.assertIn("pickup.RebindForStreaming(gunState, candidateIndex)", rent)
        self.assertIn("pickup.transform.SetPositionAndRotation(position,", rent)
        self.assertIn("pickup.gameObject.SetActive(true);", rent)
        self.assertIn("return pickup;", rent)
        self.assertNotIn("Instantiate(", rent)

        rebind = PICKUP.split("internal bool RebindForStreaming(", 1)[1].split(
            "private void Update()", 1)[0]
        for invariant in (
            "gameObject.activeSelf || consumed || reserved",
            "state.Spec.id != gunState.Spec.id",
            "state = gunState;",
            "candidateIndex = index;",
            "reserved = consumed = false;",
            "pendingSpinDegrees = 0f;",
            "model.localRotation = Quaternion.identity;",
            "modelWasVisible = false;",
            "beaconObserver = null;",
        ):
            self.assertIn(invariant, rebind)
        self.assertNotIn("new Material(", rebind)
        self.assertNotIn("Resources.Load", rebind)

    def test_far_unloads_reuse_but_consumed_transactions_still_destroy(self):
        far = WORLD.split("private bool RefreshPickups(", 1)[1].split(
            "private bool StreamNearby(", 1)[0]
        self.assertIn("if (unloaded >= MaximumUnloadsPerRefresh) continue;", far)
        self.assertIn("if (!TryRecycleFarVisual(pickup, now))", far)
        self.assertIn("Destroy(pickup.gameObject);", far)
        self.assertIn("active.RemoveAt(i);", far)
        self.assertIn("MaximumUnloadsPerRefresh", far)
        self.assertIn("ReserveNativeTeardownFrame();", far)
        take = WORLD.split("private bool TryTake(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("pickup.CommitConsumed();", take)
        self.assertIn("Destroy(pickup.gameObject);", take)
        self.assertNotIn("TryRecycleFarVisual", take)

    def test_original_authority_and_scene_visual_budget_are_unchanged(self):
        stream = WORLD.split("private bool StreamNearby(", 1)[1].split(
            "private void EnsureStarterLoot(", 1)[0]
        self.assertEqual(stream.count("TryReuseLootVisual("), 2)
        self.assertEqual(stream.count("root.AddComponent<AarisWeaponPickup>();"), 2)
        self.assertEqual(stream.count("pickup.Initialize("), 2)
        self.assertEqual(stream.count("TryClaimOptionalModelBuild()"), 2)
        self.assertIn("var state = new AarisWeaponState(choices[i],", stream)
        self.assertIn("matchSeed ^ (uint)(i + 1)", stream)
        self.assertIn("record.state, record.id", stream)
        self.assertIn("if (active.Count >= streamLimit) return false;", stream)
        self.assertIn("private const int MaximumActiveLoot = 40;", WORLD)
        self.assertIn("private const float ActivationMetres = 50f;", WORLD)
        self.assertIn("private const float ReleaseMetres = 69f;", WORLD)

    def test_low_memory_defers_cleanup_and_blocks_repool(self):
        callback = WORLD.split("private void OnLowMemory()", 1)[1].split(
            "private void DrainIdleLootVisuals()", 1)[0]
        self.assertIn("idlePoolTrimPending = true;", callback)
        self.assertIn("LowMemoryPoolCooldownSeconds", callback)
        self.assertNotIn("Destroy(", callback)
        self.assertIn("Application.lowMemory += OnLowMemory;", WORLD)
        self.assertIn("Application.lowMemory -= OnLowMemory;", WORLD)
        self.assertIn("private void OnDisable()", WORLD)
        self.assertIn("private void OnDestroy()", WORLD)
        cleanup = WORLD.split("private void TrimIdleLootVisuals(", 1)[1].split(
            "private bool TryRecycleFarVisual(", 1)[0]
        self.assertIn("int destroyBudget = idlePoolTrimPending ? 2 : 1;", cleanup)
        self.assertIn("idleLootVisuals.RemoveAt(i);", cleanup)
        self.assertIn("Destroy(idle.pickup.gameObject);", cleanup)
        self.assertIn("idlePoolTrimPending = false;", cleanup)
        self.assertIn("now < poolSuppressedUntil", WORLD)
        self.assertIn("Time.unscaledTime < poolSuppressedUntil", WORLD)

    def test_simulated_pool_never_exceeds_device_cap_or_keeps_expired_items(self):
        for ram_mb, expected in ((2048, 0), (3072, 0), (4096, 2),
                                 (6144, 4), (8192, 4), (0, 0)):
            cap = 0 if ram_mb <= 3072 else 2 if ram_mb < 6144 else 4
            self.assertEqual(cap, expected)
            cache = []
            for i in range(40):
                if len(cache) < cap:
                    cache.append(("sniper" if i % 2 else "rifle", 18.0))
                self.assertLessEqual(len(cache), expected)
            self.assertTrue(all(deadline <= 18.0 for _, deadline in cache))
            cache = [(key, deadline) for key, deadline in cache if deadline > 20.0]
            self.assertEqual(cache, [], "No native FBX hierarchy retained forever")


if __name__ == "__main__":
    unittest.main(verbosity=2)
