#!/usr/bin/env python3
"""Source contracts for pooled wheel-prefab integration on mobile.

Native device FPS / Unity build are deliberately outside this test.
"""
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text()


class WheelPrefabNativeAllocations(unittest.TestCase):
    def test_no_native_component_arrays_per_wheel(self):
        block = SOURCE.split("public bool AttachWheelModels(", 1)[1].split(
            "public bool AttachEmbeddedWheelModels(", 1)[0]
        self.assertIn("private readonly List<Collider> wheelPrefabColliders", SOURCE)
        self.assertIn("private readonly List<Renderer> wheelPrefabRenderers", SOURCE)
        self.assertIn("instance.GetComponentsInChildren<Collider>(true, wheelPrefabColliders);", block)
        self.assertIn("instance.GetComponentsInChildren<Renderer>(true, wheelPrefabRenderers);", block)
        self.assertNotIn("instance.GetComponentsInChildren<Collider>(true)", block)
        self.assertNotIn("instance.GetComponentsInChildren<Renderer>(true)", block)
        self.assertIn("finally", block)
        self.assertEqual(block.count("wheelPrefabColliders.Clear();"), 2)
        self.assertEqual(block.count("wheelPrefabRenderers.Clear();"), 2)

    def test_identical_paint_slot_arrays_cached_without_palette_leak(self):
        block = SOURCE.split("public bool AttachWheelModels(", 1)[1].split(
            "public bool AttachEmbeddedWheelModels(", 1)[0]
        self.assertIn("private readonly Dictionary<int, Material[]> wheelPrefabSlotLayouts", SOURCE)
        self.assertIn("if (!preserveAuthoredUrpMaterials && wheelPrefabLayoutPalette != palette)", block)
        self.assertIn("wheelPrefabSlotLayouts.Clear();", block)
        self.assertIn("wheelPrefabLayoutPalette = palette;", block)
        self.assertIn("wheelPrefabSlotLayouts.TryGetValue(n, out Material[] materials)", block)
        self.assertIn("wheelPrefabSlotLayouts.Add(n, materials);", block)
        self.assertIn("renderer.sharedMaterials = materials;", block)
        self.assertNotIn("renderer.materials =", block)
        self.assertEqual(block.count("materials = new Material[n];"), 1)

    def test_original_art_contact_and_physics_remain(self):
        block = SOURCE.split("public bool AttachWheelModels(", 1)[1].split(
            "public bool AttachEmbeddedWheelModels(", 1)[0]
        for token in (
            "WheelRadius", "Kenney Wheel Visual", "obstacle.enabled = false;",
            "AarisVehicleWorld.HasRenderableWheelGeometry(instance.transform)",
            "AarisVehicleWorld.TryGetVisualBoundsLocal(",
            "Mathf.Clamp(2f * WheelRadius / maxDimension, .01f, 30f)",
            "instance.transform.TransformPoint(combined.center)",
            "Quaternion.Inverse(instance.transform.rotation)",
            "visualWheelCenterOffsets[i] =",
            "visualWheels[i] = instance.transform",
            "SyncVisibleWheels();",
        ):
            self.assertIn(token, block)
        self.assertNotIn("QualitySettings.", block)
        self.assertNotIn("LODGroup", block)
        self.assertNotIn("new Material(", block)
        self.assertNotIn("Physics.IgnoreCollision", block)


if __name__ == "__main__":
    unittest.main(verbosity=2)
