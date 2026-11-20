#!/usr/bin/env python3
"""Static regression contract for low-GC Android vehicle hierarchy integration.

This does not replace Unity Editor compilation or a real handset profiler.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text("utf-8")


class AndroidVehicleSpawnAllocationBudget(unittest.TestCase):
    def test_native_queries_reuse_buffers(self):
        build = SOURCE.split("private bool Build(AarisVehicleRecord", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("private readonly List<Collider> buildColliders", SOURCE)
        self.assertIn("private readonly List<Renderer> buildRenderers", SOURCE)
        self.assertIn("mesh.GetComponentsInChildren<Collider>(true, buildColliders);", build)
        self.assertIn("mesh.GetComponentsInChildren<Renderer>(true, buildRenderers);", build)
        self.assertNotIn("mesh.GetComponentsInChildren<Collider>(true)", build)
        self.assertNotIn("mesh.GetComponentsInChildren<Renderer>(true)", build)
        self.assertGreaterEqual(build.count("buildColliders.Clear();"), 2)
        self.assertGreaterEqual(build.count("buildRenderers.Clear();"), 2)
        self.assertLess(build.index("buildRenderers.Clear();\n            PaintVerifiedBodySlots"),
                        build.index("PaintVerifiedBodySlots(mesh, record);"))

    def test_palette_array_cache_does_not_clone_materials(self):
        build = SOURCE.split("private bool Build(AarisVehicleRecord", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("private readonly Dictionary<int, Material[]> paletteSlotLayouts", SOURCE)
        self.assertIn("paletteSlotLayouts.TryGetValue(submeshes, out Material[] materials)", build)
        self.assertIn("paletteSlotLayouts.Add(submeshes, materials);", build)
        self.assertIn("materials[i] = paletteMaterial;", build)
        self.assertIn("renderer.sharedMaterials = materials;", build)
        self.assertNotIn("renderer.materials =", build)
        self.assertNotIn("new Material(", build)

    def test_preserve_fleet_distance_and_art_contracts(self):
        for token in ("private const float LoadRadius = 105f;",
                      "private const float UnloadRadius = 160f;",
                      "private const int MaximumNearbyVehicles = 10;",
                      "if (!HasRenderableVehicleBody(renderers))",
                      "ApplyAuthoredMaterials(renderers);",
                      "PaintVerifiedBodySlots(mesh, record);"):
            self.assertIn(token, SOURCE)
        self.assertIn("HasRenderableVehicleBody(IReadOnlyList<Renderer> renderers)", SOURCE)
        self.assertIn("ApplyAuthoredMaterials(IReadOnlyList<Renderer> renderers)", SOURCE)
        self.assertNotIn("QualitySettings.", SOURCE)
        self.assertNotIn("renderScale =", SOURCE)


if __name__ == "__main__":
    unittest.main()
