#!/usr/bin/env python3
"""Ground-weapon pickup zero-result-array and shader cache contract.

Source-level regression only: not a Unity/Android allocation profiler.
"""
import re
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisWeaponPickup.cs").read_text(encoding="utf-8")

def section(a,b):
    return SOURCE.split(a,1)[1].split(b,1)[0]

class PickupOneTimeNativeCost(unittest.TestCase):
    def test_shared_collider_scratch_does_not_allocate_one_array_per_pickup(self):
        self.assertIn("private static readonly List<Collider> importedColliderScratch =", SOURCE)
        self.assertIn("new List<Collider>(8)", SOURCE)
        helper=section("private static void DisableImportedColliders(", "private AarisWeaponState state;")
        self.assertIn("importedColliderScratch.Clear();",helper)
        self.assertIn("imported.GetComponentsInChildren<Collider>(true, importedColliderScratch);",helper)
        self.assertIn("for (int i = 0; i < importedColliderScratch.Count; i++)",helper)
        self.assertIn("if (collider != null) collider.enabled = false;",helper)
        self.assertIn("finally",helper)
        self.assertEqual(helper.count("importedColliderScratch.Clear();"),2)
        self.assertNotIn("GetComponentsInChildren<Collider>(true))",helper)
        self.assertNotIn("new Collider[",helper)
    def test_pickup_calls_full_safety_check_before_visual_finish(self):
        init=section("public bool Initialize(", "private void Update()")
        self.assertIn("modelRenderers = visible.GetComponentsInChildren<Renderer>(true);",init)
        self.assertIn("DisableImportedColliders(visible);",init)
        self.assertLess(init.index("DisableImportedColliders(visible);"),
                        init.index("AarisWeaponInventory.ApplyTextureAwareWeaponFinish("))
        self.assertIn("AarisWeaponMeshBounds.TryMeasure(transform,",init)
        self.assertIn("modelRenderers.Length == 0",init)
    def test_shader_lookup_occurs_only_during_shared_material_cache_miss(self):
        cache=SOURCE.split("internal static class AarisLootBeaconMaterial",1)[1]
        self.assertIn("if (shared != null) return shared;",cache)
        self.assertIn('AarisShaderCache.Find("Universal Render Pipeline/Unlit")',cache)
        self.assertIn('AarisShaderCache.Find("Sprites/Default")',cache)
        self.assertLess(cache.index("if (shared != null) return shared;"),
                        cache.index("AarisShaderCache.Find("))
        self.assertIn("if (shader == null) return null;",cache)
        self.assertIn('new Material(shader) { name = "Aaris Loot Beacon Shared" }',cache)
        self.assertIn('shared.SetColor("_BaseColor", Color.white)',cache)
        init=section("public bool Initialize(", "private void Update()")
        self.assertNotIn("AarisShaderCache.Find(",init)
        self.assertNotIn("new Material(",init)
        self.assertEqual(init.count("AarisLootBeaconMaterial.Get();"),1)
    def test_beacon_taper_and_existing_visibility_budget(self):
        init=section("public bool Initialize(", "private void Update()")
        for token in (
            "beacon.sharedMaterial = beaconPaint;",
            "beacon.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;",
            "beacon.receiveShadows = false;"):
            self.assertIn(token,init)
        self.assertIn("bool compact = state.Spec.presentationLengthMetres < 0.45f;", init)
        lower = float(re.search(r"beacon\.SetPosition\(0, new Vector3\(0f, ([0-9.]+)f, 0f\)\);", init)[1])
        heights = re.search(r"beacon\.SetPosition\(1, new Vector3\(0f, compact \? ([0-9.]+)f : ([0-9.]+)f, 0f\)\);", init)
        compact_height, long_height = map(float, heights.groups())
        widths = re.search(r"beacon\.startWidth = compact \? ([0-9.]+)f : ([0-9.]+)f;", init)
        compact_width, long_width = map(float, widths.groups())
        tip = float(re.search(r"beacon\.endWidth = ([0-9.]+)f;", init)[1])
        self.assertLessEqual(0.05, lower)
        self.assertLessEqual(lower, 0.15)
        self.assertLessEqual(0.45, long_height)
        self.assertLessEqual(long_height, 0.80)
        self.assertLess(long_height, compact_height)
        self.assertLessEqual(compact_height, 0.90)
        self.assertLessEqual(0.035, long_width)
        self.assertLess(long_width, compact_width)
        self.assertLessEqual(compact_width, 0.076)
        self.assertLess(tip, long_width / 4)
        self.assertRegex(init, r"beacon\.endColor = new Color\([^,]+, [^,]+, [^,]+, (?:0f|0\.0[1-5]f)\);")
        self.assertIn("beaconObserver = FindBeaconCamera();",init)
        self.assertIn("activePickups.Add(this);",init)
    def test_shared_refs_cleared_when_unity_subsystem_resets(self):
        reset=section("private static void ClearPickupRegistry()", "private static Camera FindBeaconCamera()")
        self.assertIn("importedColliderScratch.Clear();",reset)
        self.assertIn("activePickups.Clear();",reset)
        self.assertIn("sharedBeaconCamera = null;",reset)
        self.assertIn("RuntimeInitializeLoadType.SubsystemRegistration",SOURCE)
    def test_no_per_frame_added_component_or_shader_queries(self):
        update=section("private void Update()", "private void OnEnable()")
        self.assertNotIn("GetComponentsInChildren",update)
        self.assertNotIn("Shader.Find",update)
        self.assertNotIn("new Material",update)
        self.assertIn("nextBeaconVisibilityCheck = Time.unscaledTime + 0.45f;",update)
        self.assertNotIn("model.Rotate(", update)
        self.assertNotIn("isVisible", update)

if __name__ == "__main__":
    unittest.main(verbosity=2)
