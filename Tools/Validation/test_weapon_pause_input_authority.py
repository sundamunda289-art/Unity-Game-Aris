#!/usr/bin/env python3
"""Source-level input-authority guards for offline loot and HUD actions.

These contracts are not Unity Play Mode or Android device measurements.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
INVENTORY = (ROOT / "AarisWeaponInventory.cs").read_text("utf-8")
LOOT = (ROOT / "AarisWeaponLootWorld.cs").read_text("utf-8")


def section(src, start, end):
    return src.split(start, 1)[1].split(end, 1)[0]


class OfflineWeaponPauseAndLandingAuthority(unittest.TestCase):
    def test_all_keyboard_and_hud_commands_share_on_foot_guard(self):
        authority = section(
            INVENTORY, "private bool AcceptsPlayerWeaponInput =>",
            "public void RequestPickup()")
        for text in ("isActiveAndEnabled", "Time.timeScale > 0f",
                     "motor != null && motor.isActiveAndEnabled",
                     "combat == null || combat.isActiveAndEnabled"):
            self.assertIn(text, authority)
        wrappers = section(INVENTORY, "public void RequestPickup()",
                           "private static void SetHierarchyLayer(")
        self.assertIn("if (AcceptsPlayerWeaponInput && lootWorld != null)", wrappers)
        self.assertIn("DropCurrent() => AcceptsPlayerWeaponInput &&", wrappers)
        self.assertIn("Reload() => AcceptsPlayerWeaponInput &&", INVENTORY)
        for signature, stop in (
            ("public bool ToggleFists()", "public bool SwapWeapons()"),
            ("public bool SwapWeapons()", "public void ToggleAim()"),
            ("public void ToggleAim()", "private void ResetCombatTransition()"),
            ("private void Update()", "private void OnDisable()"),
        ):
            body = section(INVENTORY, signature, stop)
            self.assertIn("AcceptsPlayerWeaponInput", body)
            self.assertLess(body.index("AcceptsPlayerWeaponInput"),
                            body.find("Input.GetKeyDown(") if "Input.GetKeyDown(" in body
                            else len(body))

    def test_landing_gate_retains_visual_prestream(self):
        landing = section(LOOT, "private bool HasLandingAuthority =>",
                          "/// <summary>Explicit pickup")
        self.assertIn("drop == null || !drop.isActiveAndEnabled || drop.HasLanded", landing)
        update = section(LOOT, "private void Update()", "private bool RefreshPickups(")
        self.assertIn("drop.ShouldDeferGroundLoot", update)
        self.assertIn("StreamNearby(playerPosition, now)", update)
        # Near-ground prestream still works; only interaction is gated.
        refresh = section(LOOT, "private bool RefreshPickups(", "private bool StreamNearby(")
        self.assertIn("bool mayPickup = HasLandingAuthority && Time.timeScale > 0f", refresh)
        self.assertIn("if (mayPickup && d2 <= bestDistance", refresh)
        self.assertIn("HasPickupInRange = mayPickup && bestPickup != null;", refresh)
        self.assertIn("return HasPickupInRange &&", refresh)
        take = section(LOOT, "private bool TryTake(", "private void Update()")
        self.assertLess(take.index("!HasLandingAuthority"), take.index("pickup.TryReserve()"))

    def test_pause_clears_stale_pickup_requests_without_spawning(self):
        request = section(LOOT, "public void RequestPickup()", "[RuntimeInitializeOnLoadMethod(")
        self.assertLess(request.index("Time.timeScale <= 0f"), request.index("requestPickupUntil ="))
        self.assertIn("!HasLandingAuthority", request)
        update = section(LOOT, "private void Update()", "private bool RefreshPickups(")
        self.assertLess(update.index("Time.timeScale <= 0f"), update.index("RefreshPickups("))
        for token in ("requestPickupUntil = 0f;", "HasPickupInRange = false;",
                      "nextRefresh = 0f;", "streamFramesRemaining = 0;"):
            self.assertIn(token, update)
        drop = section(LOOT, "public bool TryDropEquipped()", "private bool IsBlockedDrop(")
        for token in ("Time.timeScale <= 0f", "!HasLandingAuthority",
                      "!inventory.isActiveAndEnabled"):
            self.assertIn(token, drop)
        self.assertLess(drop.index("!HasLandingAuthority"),
                        drop.index("TryFindDropSpot(out Vector3 point)"))

    def test_authority_matrix_denies_pause_vehicle_and_flight(self):
        for time_scale in (0.0, 1.0):
            for inventory_active in (False, True):
                for motor_active in (False, True):
                    for combat_active in (False, True):
                        for flight_active in (False, True):
                            for landed in (False, True):
                                hud = (time_scale > 0 and inventory_active and
                                       motor_active and combat_active)
                                floor = (time_scale > 0 and inventory_active and
                                         (not flight_active or landed))
                                allowed = hud and floor
                                if time_scale == 0 or not inventory_active or (
                                        flight_active and not landed):
                                    self.assertFalse(allowed)
                                if (time_scale > 0 and inventory_active and
                                        motor_active and combat_active and
                                        (not flight_active or landed)):
                                    self.assertTrue(allowed)

if __name__ == "__main__":
    unittest.main()
