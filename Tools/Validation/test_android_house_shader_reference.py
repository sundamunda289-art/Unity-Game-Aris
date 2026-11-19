#!/usr/bin/env python3
"""Source regression for safe prefab-referenced house shader resolution.

Does not claim that Shader.Find alone causes Android driver stalls.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisModularHouse.cs").read_text()
EXPANSION = (ROOT / "Assets/AarisWorld/Scripts/AarisSettlementExpansion.cs").read_text()
CATALOG = (ROOT / "Assets/AarisWorld/Scripts/AarisExpansionCatalog.cs").read_text()


class HouseShaderReference(unittest.TestCase):
    def test_prepare_uses_referenced_prefab_shader_before_old_safe_fallback(self):
        prepare = SOURCE.split("private static bool Prepare(", 1)[1].split(
            "private static Material Match(", 1)[0]
        self.assertIn("GameObject originalPrefab, bool includePanels", prepare)
        self.assertIn("if (wall != null && roof != null && trim != null &&", prepare)
        self.assertIn("var source = originalPrefab.GetComponentInChildren<MeshRenderer>(true);", prepare)
        self.assertIn("foreach (Material material in source.sharedMaterials)", prepare)
        self.assertIn('material.shader.name == "Universal Render Pipeline/Lit"', prepare)
        self.assertIn("shader = material.shader;", prepare)
        self.assertIn('if (shader == null)\n                shader = Shader.Find("Universal Render Pipeline/Lit");', prepare)
        self.assertLess(prepare.index("shader = material.shader;"),
                        prepare.index('Shader.Find("Universal Render Pipeline/Lit")'))
        self.assertIn("if (shader == null) return false;", prepare)

    def test_house_geometry_and_materials_unchanged(self):
        self.assertIn("!Prepare(originalPrefab, includePanels)) return null;", SOURCE)
        self.assertIn("new Material(shader) { name = name, enableInstancing = true }", SOURCE)
        self.assertIn('m.SetColor("_BaseColor", tint);', SOURCE)
        self.assertIn("ApplySharedMasonryMaps(wall);", SOURCE)
        self.assertIn("wall = Match(originalPrefab,", SOURCE)
        self.assertIn("roof = Match(originalPrefab,", SOURCE)
        self.assertIn("trim = Match(originalPrefab,", SOURCE)
        self.assertIn("AarisModularHouse.Create(position, rotation", EXPANSION)
        self.assertIn("public GameObject openCottage;", CATALOG)
        self.assertIn("public GameObject openBungalow;", CATALOG)
        self.assertIn("public GameObject openDetached;", CATALOG)

    def test_no_automatic_shader_warmup_or_scene_quality_downgrade(self):
        self.assertNotIn("ShaderVariantCollection.WarmUp()", SOURCE)
        self.assertNotIn("QualitySettings.skinWeights", SOURCE)
        self.assertNotIn("GraphicsSettings.defaultRenderPipeline =", SOURCE)
        self.assertNotIn("StaticBatchingUtility.Combine", SOURCE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
