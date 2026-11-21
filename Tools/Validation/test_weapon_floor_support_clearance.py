#!/usr/bin/env python3
"""Source contract: terrain grass lift must not make road/floor loot float.

This does not replace Unity 6000.3 Play Mode mesh/floor photo verification.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs").read_text()
PICKUP = (ROOT / "Assets/AarisWorld/Scripts/AarisWeaponPickup.cs").read_text()
AUDIT = (ROOT / "Assets/AarisWorld/Editor/AarisGunDoorLiveAudit.cs").read_text()


def clearance(name):
    match = re.search(r"private const float " + name + r" = ([0-9.]+)f;", WORLD)
    if not match:
        raise AssertionError("Missing placement constant: " + name)
    return float(match.group(1))


class WeaponFloorSupportContract(unittest.TestCase):
    def test_grass_visibility_lift_is_preserved(self):
        self.assertGreater(clearance("OutdoorLootPresentationClearance"),
                           clearance("LootRootFloorClearance"))

    def test_terrain_only_receives_outdoor_lift(self):
        self.assertIn("bool terrainGrassSupport = !indoor && floor.collider is TerrainCollider;", WORLD)
        self.assertIn("spot = floor.point + Vector3.up * (terrainGrassSupport\n"
                      "                ? OutdoorLootPresentationClearance : LootRootFloorClearance);", WORLD)

    def test_indoor_and_solid_support_keep_ground_contact(self):
        self.assertAlmostEqual(clearance("LootRootFloorClearance"), 0.04)
        self.assertIn("floor.collider is TerrainCollider", WORLD)
        # Mesh's additional 2.5 cm is applied identically to all support types.
        self.assertIn("ModelFloorClearance = 0.025f;", PICKUP)
        self.assertAlmostEqual(clearance("LootRootFloorClearance") + 0.025, 0.065)

    def test_near_grade_solid_support_outranks_underlying_terrain(self):
        # Real PhysX proof lives in AarisWeaponSupportPhysicsQA.Run.
        # This contract prevents accidental deletion of the selection rule.
        self.assertIn("bool preferredNearGradeSolid = false;", WORLD)
        self.assertIn("bool nearGradeSolid = !indoor &&", WORLD)
        self.assertIn("candidate.point.y >= expectedGround - 0.02f", WORLD)
        self.assertIn("candidate.point.y <= expectedGround + 0.22f", WORLD)
        self.assertIn("if (preferredNearGradeSolid && !nearGradeSolid) continue;", WORLD)
        self.assertIn("preferredNearGradeSolid = nearGradeSolid;", WORLD)
        self.assertLess(
            WORLD.index("if (preferredNearGradeSolid && !nearGradeSolid) continue;"),
            WORLD.index("preferredNearGradeSolid = nearGradeSolid;"))

    def test_native_camera_clip_is_restored_after_each_photo(self):
        # A 2 km map renders too many distant objects with software OpenGL.
        # The QA-only crop must restore original gameplay camera settings.
        self.assertIn("float previousFarClip = camera.farClipPlane;", AUDIT)
        self.assertIn("camera.farClipPlane = showTerrain ? 20f : 8f;", AUDIT)
        self.assertIn("camera.farClipPlane = previousFarClip;", AUDIT)

    def test_no_extra_physics_or_allocation_on_placement(self):
        tail = WORLD.split("bool terrainGrassSupport = !indoor && floor.collider is TerrainCollider;", 1)[1]
        tail = tail.split("return true;", 1)[0]
        self.assertNotIn("Physics.", tail)
        self.assertNotIn("new ", tail)
        self.assertNotIn("Instantiate", tail)


if __name__ == "__main__":
    unittest.main()
