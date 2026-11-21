#!/usr/bin/env python3
"""Regression contracts for zero-array imported-wheel and URP validator."""
import unittest
from pathlib import Path

CODE = (Path(__file__).resolve().parents[2]/
    "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text("utf8")


class VehicleRendererScratchTests(unittest.TestCase):
    def test_imported_wheel_uses_reusable_renderers(self):
        section=CODE.split("internal static bool HasRenderableWheelGeometry(",1)[1].split(
            "internal static bool TryGetVisualBoundsLocal(",1)[0]
        for token in (
            "[ThreadStatic] private static List<Renderer> validationRenderers;",
            "wheelRoot.gameObject.activeInHierarchy",
            "wheelRoot.GetComponentsInChildren<Renderer>(true, renderers);",
            "return HasRenderableVehicleBody(renderers);",
            "finally", "renderers.Clear();"
        ):
            self.assertIn(token,CODE)
        self.assertNotIn("wheelRoot.GetComponentsInChildren<Renderer>(true)",section)
        self.assertEqual(section.count("renderers.Clear();"),2)

    def test_urp_materials_without_per_renderer_array(self):
        part=CODE.split("private static bool HasCompatibleUrpMaterials(GameObject source)",1)[1].split(
            "private int TypeIndex(",1)[0]
        for token in (
            "source.GetComponentsInChildren<Renderer>(true, renderers);",
            "renderer.GetSharedMaterials(materials);",
            "if (materials.Count == 0) return false;",
            "if (renderer == null) return false;",
            'material.shader.name != "Universal Render Pipeline/Lit"',
            'material.HasProperty("_BaseColor")',
            'material.HasProperty("_Metallic")',
            'material.HasProperty("_Smoothness")',
            "finally", "materials.Clear();", "renderers.Clear();"):
            self.assertIn(token,part)
        self.assertNotIn("renderer.sharedMaterials",part)
        self.assertNotIn("GetComponentsInChildren<Renderer>(true);",part)
        self.assertIn("[ThreadStatic] private static List<Material> validationMaterials;",CODE)
        self.assertEqual(part.count("materials.Clear();"),2)
        self.assertEqual(part.count("renderers.Clear();"),2)

    def test_original_true_mesh_visibility_guard(self):
        section=CODE.split("private static bool HasRenderableVehicleBody(",1)[1].split(
            "internal static bool HasRenderableWheelGeometry(",1)[0]
        for token in ("renderer.forceRenderingOff", "renderer.gameObject.activeInHierarchy",
                      "skinned.sharedMesh.vertexCount > 0",
                      "filter.sharedMesh.vertexCount > 0"):
            self.assertIn(token,section)


if __name__=="__main__":
    unittest.main(verbosity=2)
