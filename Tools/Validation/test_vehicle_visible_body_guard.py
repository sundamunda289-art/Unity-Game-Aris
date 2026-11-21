#!/usr/bin/env python3
"""Contract checks for visible vehicle bodies and cached support-ray terrain."""
import unittest
from pathlib import Path

W = (Path(__file__).resolve().parents[2] /
     "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text("utf-8")


class VehicleVisibleBodyGuard(unittest.TestCase):
    def test_disabled_inactive_and_empty_meshes_are_not_counted(self):
        helper = W.split("private static bool HasRenderableVehicleBody(", 1)[1].split(
            "internal static bool TryGetVisualBoundsLocal(", 1)[0]
        for requirement in ("!renderer.enabled", "renderer.forceRenderingOff",
                            "!renderer.gameObject.activeInHierarchy",
                            "skinned.sharedMesh.vertexCount > 0",
                            "filter.sharedMesh.vertexCount > 0"):
            self.assertIn(requirement, helper)
        self.assertIn("return false;", helper)

    def test_broken_visible_model_falls_back_before_claiming_spawn(self):
        build = W.split("private bool Build(", 1)[1].split("private void Update()", 1)[0]
        check = build.index("if (!HasRenderableVehicleBody(renderers))")
        self.assertIn('FallBackToVerifiedModel(typeIndex, "renderer check");', build)
        self.assertLess(check, build.index("var motor = root.AddComponent<AarisVehicleMotor>();"))
        self.assertLess(check, build.index("record.visible = motor;"))

    def test_cached_terrain_queries_and_late_loaded_tile_refresh(self):
        self.assertIn("private Terrain[] terrainCache;", W)
        self.assertIn("private float nextTerrainRefresh;", W)
        self.assertEqual(W.count("Terrain.activeTerrains"), 2)
        lookup = W.split("private Terrain TerrainAt(", 1)[1].split(
            "private bool HasSupport(", 1)[0]
        for requirement in ("FindTerrain(terrainCache, x, z)",
                            "Time.unscaledTime < nextTerrainRefresh",
                            "nextTerrainRefresh = Time.unscaledTime + 5f;",
                            "terrainCache = Terrain.activeTerrains;"):
            self.assertIn(requirement, lookup)
        self.assertIn("!terrain.isActiveAndEnabled", W)

    def test_wheel_geometry_guard_reuses_renderable_mesh_validation(self):
        helper = W.split("internal static bool HasRenderableWheelGeometry(", 1)[1].split(
            "internal static bool TryGetVisualBoundsLocal(", 1)[0]
        self.assertIn("wheelRoot.gameObject.activeInHierarchy", helper)
        self.assertIn("HasRenderableVehicleBody(", helper)
        self.assertIn("wheelRoot.GetComponentsInChildren<Renderer>(true, renderers);", helper)
        self.assertIn("HasRenderableVehicleBody(renderers)", helper)
        self.assertIn("renderers.Clear();", helper)

    def test_embedded_and_separate_wheels_require_visible_geometry(self):
        motor = (Path(__file__).resolve().parents[2] /
                 "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs").read_text("utf-8")
        separate = motor.split("public bool AttachWheelModels(", 1)[1].split(
            "public bool AttachEmbeddedWheelModels(", 1)[0]
        embedded = motor.split("public bool AttachEmbeddedWheelModels(", 1)[1].split(
            "private static Renderer CreateLampLens(", 1)[0]
        self.assertIn("HasRenderableWheelGeometry(instance.transform)", separate)
        self.assertIn("HasRenderableWheelGeometry(found[i])", embedded)
        self.assertLess(separate.index("HasRenderableWheelGeometry("),
                        separate.index("visualWheels[i] = instance.transform;"))
        self.assertLess(embedded.index("HasRenderableWheelGeometry("),
                        embedded.index("wheelVisual.SetParent(transform, true);"))



if __name__ == "__main__":
    unittest.main()
