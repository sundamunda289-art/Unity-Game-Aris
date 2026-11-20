#!/usr/bin/env python3
"""Source contracts for renderer-topology-verified immutable bike paint layouts.

Not an Android FPS benchmark or a replacement for native Unity rendering.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisRoadsideBikeScenery.cs").read_text(encoding="utf-8")


class BikePaintLayoutCache(unittest.TestCase):
    def test_cached_only_after_original_paint_mapping_is_ready(self):
        build = SOURCE.split("private Material[][][] GetBikePaintLayouts(Renderer[] renderers)", 1)[1].split(
            "private GameObject CreateBike(", 1)[0]
        self.assertIn("if (MatchesCachedBikeSource(renderers))", build)
        self.assertIn("return cachedBikePaintLayouts;", build)
        self.assertIn("cachedBikePaintLayouts = layouts;", build)
        self.assertIn("cachedBikePrefabMaterials = sourceSlots;", build)
        self.assertLess(build.index("kind == 0 ? paintVariants[paint] : surfaces[kind]"),
                        build.index("cachedBikePaintLayouts = layouts;"))
        self.assertIn("if (cachedBikePaintLayouts == null)", build)

    def test_no_per_new_bike_paint_layout_arrays_after_match(self):
        maker = SOURCE.split("private GameObject CreateBike(", 1)[1].split(
            "private void OnDestroy()", 1)[0]
        self.assertIn("Material[][][] layouts = GetBikePaintLayouts(renderers);", maker)
        self.assertNotIn("new Material[paintVariants.Length][][]", maker)
        self.assertNotIn("new int[source.Length]", maker)
        self.assertNotIn("new Material[source.Length]", maker)
        self.assertIn("renderer.sharedMaterials = layouts[paintIndex][r];", maker)
        self.assertIn("Renderer[] renderers = imported.GetComponentsInChildren<Renderer>(true);", maker)

    def test_unexpected_prefab_source_materials_force_safe_fallback(self):
        match = SOURCE.split("private bool MatchesCachedBikeSource(Renderer[] renderers)", 1)[1].split(
            "private Material[][][] GetBikePaintLayouts", 1)[0]
        self.assertIn("renderers.Length != cachedBikePrefabMaterials.Length", match)
        self.assertIn("renderer == null", match)
        self.assertIn("renderer.GetSharedMaterials(bikeSourceScratch);", match)
        self.assertIn("original.Length != bikeSourceScratch.Count", match)
        self.assertIn("original[i] != bikeSourceScratch[i]", match)
        self.assertNotIn("renderer.sharedMaterials", match)
        self.assertIn("private readonly List<Material> bikeSourceScratch = new List<Material>(8);", SOURCE)

    def test_no_new_palette_mesh_or_visual_changes(self):
        markers = (
            "private const int MaximumVisibleBikes = 3;",
            "private const float LoadRadius = 45f;",
            "private const float PrewarmRadius = 110f;",
            "private const float UnloadRadius = 66f;",
            "AarisVehicles/Authored/AarisRoadBike",
            "int[] fallbackOrder = {1,2,3,4,0,7,5,6};",
            "StablePaintIndex(point.id)",
            "kind == 0 ? paintVariants[paint] : surfaces[kind]",
            "new LOD(.012f, renderers)",
            "ReuseBike(parkedBikePool.Pop(), nearest.point, spot)",
        )
        for token in markers:
            self.assertIn(token, SOURCE)
        self.assertEqual(SOURCE.count("new Material(shader)"), 1)
        self.assertEqual(SOURCE.count("new Material(surfaces[0])"), 1)
        self.assertNotIn("renderer.materials =", SOURCE)
        self.assertNotIn("new Material(", SOURCE.split("private GameObject CreateBike(", 1)[1])


if __name__ == "__main__":
    unittest.main(verbosity=2)
