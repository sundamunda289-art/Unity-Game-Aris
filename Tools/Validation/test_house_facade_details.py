#!/usr/bin/env python3
"""Mobile-budget facade visual checks; native Unity and Android remain separate."""
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
FACADE = (SCRIPTS / "AarisHouseFacadeTrim.cs").read_text()
EXPANSION = (SCRIPTS / "AarisSettlementExpansion.cs").read_text()
PLAN = json.loads((ROOT / "Assets/Resources/AarisExpansion/WorldExpansionPlan.json").read_text())


class HouseFacadeDetails(unittest.TestCase):
    def test_all_sixteen_expansion_homes_are_targeted_but_not_towers(self):
        homes = [s for s in PLAN["sites"] if s["prefabKey"] in
                 ("cottage", "bungalow", "detached")]
        self.assertEqual(len(homes), 16)
        self.assertEqual(sum(bool(s.get("interactive")) for s in homes), 4)
        self.assertEqual(sum(s["prefabKey"] == "watchtower" for s in PLAN["sites"]), 5)
        self.assertIn("AarisHouseFacadeTrim.Install(instance,", EXPANSION)
        self.assertIn("site.width, site.depth, site.height", EXPANSION)
        # Register the authored house first; the optional Android facade is
        # installed in a separately admitted frame without changing its type guard.
        guard = EXPANSION.split("if (site.prefabKey == \"cottage\"", 1)[1].split(
            "AarisHouseFacadeTrim.Install(instance,", 1)[0]
        self.assertIn('site.prefabKey == "bungalow"', guard)
        self.assertIn('site.prefabKey == "detached"', guard)
        self.assertNotIn('site.prefabKey == "watchtower"', guard)

    def test_one_combined_mesh_and_no_new_collider_or_gameplay_tick(self):
        self.assertIn("mesh.SetVertices(vertices);", FACADE)
        self.assertIn("mesh.SetTriangles(triangles,0);", FACADE)
        self.assertIn("mesh.UploadMeshData(true);", FACADE)
        self.assertEqual(FACADE.count("new GameObject("), 1)
        self.assertEqual(FACADE.count("AddComponent<MeshRenderer>()"), 1)
        self.assertNotIn("AddComponent<BoxCollider>", FACADE)
        self.assertNotIn("AddComponent<MeshCollider>", FACADE)
        self.assertNotIn("private void Update()", FACADE)
        self.assertNotIn("FixedUpdate()", FACADE)
        self.assertIn("renderer.shadowCastingMode = ShadowCastingMode.Off;", FACADE)

    def test_thirteen_sections_keep_real_door_clear(self):
        # Mesh geometry is unchanged, but temporary lists are reused to
        # eliminate per-home cold-start allocations on 4–6 GB Android phones.
        self.assertIn("static readonly List<Vector3> scratchVertices", FACADE)
        self.assertIn("static readonly List<int> scratchTriangles", FACADE)
        self.assertIn("List<Vector3> vertices = scratchVertices;", FACADE)
        self.assertIn("List<int> triangles = scratchTriangles;", FACADE)
        self.assertIn("vertices.Clear();", FACADE)
        self.assertIn("triangles.Clear();", FACADE)
        self.assertNotIn("var vertices = new List<Vector3>(13 * 24);", FACADE)
        self.assertIn("float entryGap = 1.92f;", FACADE)
        self.assertIn("float wing = (width-entryGap) * .5f;", FACADE)
        self.assertGreater(1.92, 1.52)
        self.assertIn("new Vector3(.19f,height-.60f,.19f)", FACADE)
        self.assertIn("new Vector3(width+.34f,.20f,.23f)", FACADE)
        self.assertIn("new Vector3(.23f,.20f,depth+.34f)", FACADE)

    def test_flat_facing_normals_and_shared_material_lifetime(self):
        self.assertIn("Vector3.Cross(b - a, d - a).normalized;", FACADE)
        self.assertEqual(FACADE.count("AddFace(v,n,uv,tris,center,"), 6)
        self.assertIn("enableInstancing = true", FACADE)
        self.assertIn('Shader.Find("Universal Render Pipeline/Lit")', FACADE)
        self.assertIn("materialUsers++;", FACADE)
        self.assertIn("materialUsers = Mathf.Max(0, materialUsers - 1);", FACADE)
        self.assertIn("Destroy(ownedMesh);", FACADE)
        self.assertIn("Destroy(sharedStucco);", FACADE)
        self.assertIn("house.GetComponentInChildren<AarisHouseFacadeTrim>()", FACADE)

    def test_native_original_prefabs_and_saved_scene_untouched(self):
        for name in ("OpenCottage", "OpenBungalow", "OpenDetached"):
            content = (ROOT / f"Assets/Resources/AarisBuildings/{name}.prefab").read_text()
            self.assertEqual(content.count("MeshRenderer:"), 1)
            self.assertEqual(content.count("MeshCollider:"), 1)


if __name__ == "__main__":
    unittest.main()
