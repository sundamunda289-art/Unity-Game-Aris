#!/usr/bin/env python3
"""Static source audit for additive offline house/watchtower expansion.

This is deliberately a license-free prerequisite. It does NOT assert that
Unity imported, rendered, physically placed or baked the new houses.
"""
import json
import math
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
LAYOUT = ROOT / "Docs" / "MAP_LAYOUT.json"
PLAN = ROOT / "Assets" / "Resources" / "AarisExpansion" / "WorldExpansionPlan.json"
CATALOG = ROOT / "Assets" / "Resources" / "AarisExpansion" / "AarisExpansionCatalog.asset"
SCRIPT = ROOT / "Assets" / "AarisWorld" / "Scripts" / "AarisSettlementExpansion.cs"
MODELS = {
    "cottage": "pitched_roof_cottage",
    "bungalow": "bungalow_house",
    "detached": "detached_two_storey_house",
    "watchtower": "watchtower",
}


def distance_segment(px, pz, a, b):
    ax, _, az = a
    bx, _, bz = b
    dx, dz = bx - ax, bz - az
    denom = dx * dx + dz * dz
    t = max(0.0, min(1.0, ((px - ax) * dx + (pz - az) * dz) / denom)) if denom else 0.0
    return math.hypot(px - (ax + t * dx), pz - (az + t * dz))


def aabb(site, margin=0.0):
    width, depth = float(site["width"]), float(site["depth"])
    if int(site["yaw"]) % 180 == 90:
        width, depth = depth, width
    x, z = float(site["x"]), float(site["z"])
    return [x-width/2-margin, z-depth/2-margin,
            x+width/2+margin, z+depth/2+margin]


def overlap(a, b):
    return a[0] < b[2] and a[2] > b[0] and a[1] < b[3] and a[3] > b[1]


class ExpansionIntegrity(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.world = json.loads(LAYOUT.read_text(encoding="utf-8"))
        cls.plan = json.loads(PLAN.read_text(encoding="utf-8"))
        cls.catalog = CATALOG.read_text(encoding="utf-8")

    def test_schema_and_district_distribution(self):
        p = self.plan
        self.assertEqual(p["schemaVersion"], 1)
        self.assertEqual(p["mapSizeMetres"], 2000)
        self.assertEqual(p["targetGameScene"],
                         "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity")
        self.assertEqual(p["siteCount"], len(p["sites"]))
        self.assertEqual(len(p["sites"]), 21)
        self.assertEqual(sum(s["prefabKey"] == "watchtower" for s in p["sites"]), 5)
        self.assertEqual(sum(s["prefabKey"] != "watchtower" for s in p["sites"]), 16)
        self.assertGreaterEqual(len(set(s["district"] for s in p["sites"])), 10)
        self.assertEqual(len(set(s["name"] for s in p["sites"])), len(p["sites"]))

    def test_original_local_prefabs_resolve_by_guid(self):
        script_meta = (ROOT / "Assets" / "AarisWorld" / "Scripts" /
                       "AarisExpansionCatalog.cs.meta").read_text(encoding="utf-8")
        script_guid = re.search(r"^guid:\s*([0-9a-f]{32})$", script_meta, re.M).group(1)
        self.assertIn(script_guid, self.catalog)
        self.assertIn("AarisExpansionCatalog", self.catalog)
        catalog_source = (ROOT / "Assets" / "AarisWorld" / "Scripts" /
                          "AarisExpansionCatalog.cs").read_text(encoding="utf-8")
        self.assertIn("sealed class AarisExpansionCatalog : ScriptableObject", catalog_source)
        self.assertIn("mainObjectFileID: 11400000", CATALOG.with_suffix(".asset.meta").read_text())
        for key, model in MODELS.items():
            path = ROOT / "Assets" / "AarisWorld" / "Models" / model / f"{model}.prefab"
            self.assertTrue(path.is_file(), str(path))
            meta = path.with_suffix(".prefab.meta")
            guid = re.search(r"^guid:\s*([0-9a-f]{32})$", meta.read_text(), re.M).group(1)
            self.assertRegex(self.catalog, rf"(?m)^  {key}: \{{fileID: 10000, guid: {guid}, type: 3\}}$")
        self.assertNotIn("http://", self.catalog)
        self.assertNotIn("https://", self.catalog)

    def test_mobile_lod_and_solid_colliders_for_each_reused_model(self):
        total_lod0 = 0
        for site in self.plan["sites"]:
            model = MODELS[site["prefabKey"]]
            total_lod0 += self.world["models"][model]["triangles"]
        # New buildings are rendered with their ORIGINAL THREE-LEVEL LOD,
        # not 15 freshly duplicated high-poly mesh assets.
        self.assertLess(total_lod0, 240_000)
        for model in set(MODELS.values()):
            path = ROOT / "Assets" / "AarisWorld" / "Models" / model / f"{model}.prefab"
            text = path.read_text(encoding="utf-8")
            self.assertEqual(text.count("LODGroup:"), 1, model)
            self.assertGreaterEqual(text.count("MeshRenderer:"), 3, model)
            self.assertGreaterEqual(text.count("MeshFilter:"), 3, model)
            self.assertGreaterEqual(text.count("MeshCollider:"), 1, model)

    def test_ground_anchors_and_bounds(self):
        districts = {d["name"]: d for d in self.world["districts"]}
        for s in self.plan["sites"]:
            with self.subTest(site=s["name"]):
                d = districts[s["district"]]
                self.assertTrue(s["name"].startswith("ARX_"))
                self.assertIn(s["prefabKey"], MODELS)
                vals = [s[n] for n in ("x", "z", "yaw", "groundReferenceY", "width",
                                       "depth", "height")]
                self.assertTrue(all(isinstance(x, (float, int)) and math.isfinite(x)
                                    for x in vals))
                self.assertGreater(s["groundReferenceY"], self.world["sea_level_m"] + 3)
                self.assertLess(abs(s["groundReferenceY"] - d["height"]), 0.01)
                self.assertTrue(-950 < s["x"] < 950 and -950 < s["z"] < 950)
                box = aabb(s, margin=2.0)
                self.assertGreater(box[0], d["x"] - d["width"]/2)
                self.assertLess(box[2], d["x"] + d["width"]/2)
                self.assertGreater(box[1], d["z"] - d["depth"]/2)
                self.assertLess(box[3], d["z"] + d["depth"]/2)
                model = self.world["models"][MODELS[s["prefabKey"]]]
                dims = model["dimensions_m"]
                scale = model["prefab_scale"]
                self.assertAlmostEqual(s["width"], dims[0] * scale, delta=0.004)
                self.assertAlmostEqual(s["depth"], dims[2] * scale, delta=0.004)
                self.assertAlmostEqual(s["height"], dims[1] * scale, delta=0.004)

    def test_no_overlap_with_original_buildings_or_other_expansions(self):
        placed = self.plan["sites"]
        for i, site in enumerate(placed):
            box = aabb(site, margin=3.2)
            for existing in self.world["buildings"]:
                self.assertFalse(overlap(box, existing["aabb_xz_m"]),
                                 (site["name"], existing["name"]))
            for other in placed[i+1:]:
                self.assertFalse(overlap(box, aabb(other, margin=3.2)),
                                 (site["name"], other["name"]))

    def test_road_clearance_and_vehicle_bays(self):
        for site in self.plan["sites"]:
            cx, cz = site["x"], site["z"]
            radius = math.hypot(site["width"], site["depth"]) * 0.5
            for road in self.world["roads"]:
                points = road["points"]
                nearest = min(distance_segment(cx, cz, p1, p2)
                              for p1, p2 in zip(points, points[1:]))
                self.assertGreater(nearest, radius + road["width"]/2 + 4.5,
                                   (site["name"], road["name"], round(nearest, 2)))
            for bay in self.world["vehicle_spawns"]:
                px, _, pz = bay["position_m"]
                reserved = bay["reserved_size_m"]
                clearance = math.hypot(cx-px, cz-pz) - radius - math.hypot(*reserved)/2
                self.assertGreater(clearance, 5.0,
                                   (site["name"], bay["name"], round(clearance, 2)))

    def test_no_forest_tree_overlap_at_authored_sites(self):
        forest = json.loads((ROOT / "Docs" / "FOREST_PLANTING_PLAN.json")
                            .read_text(encoding="utf-8"))
        self.assertEqual(forest["schema_version"], 1)
        for site in self.plan["sites"]:
            margin_x = site["width"] * 0.5 + 2.0
            margin_z = site["depth"] * 0.5 + 2.0
            # Original forest uses native TerrainData tree instances (not GameObjects).
            for tree in forest["placements"]:
                x, z = tree["position_xz_m"]
                buffer = min(3.8, max(0.5, tree["width_scale"] * 1.5))
                self.assertFalse(abs(x-site["x"]) < margin_x+buffer and
                                 abs(z-site["z"]) < margin_z+buffer,
                                 (site["name"], tree["id"]))

    def test_no_unsafe_changes_to_native_map_or_network(self):
        script = SCRIPT.read_text(encoding="utf-8")
        for required in ["Terrain.activeTerrains", "GetInterpolatedHeight", "GetSteepness",
                         "TreeInstance", "Physics.OverlapBoxNonAlloc",
                         "Resources.Load<AarisExpansionCatalog>",
                         "Resources.Load<TextAsset>",
                         "RuntimeInitializeOnLoadMethod"]:
            self.assertIn(required, script)
        for banned in ["Unity.Netcode", "NetworkBehaviour", "Application.OpenURL",
                       "WebRequest", "TerrainData.SetHeights", "DestroyImmediate"]:
            self.assertNotIn(banned, script)
        self.assertIn("height = origin.y + t.terrainData.GetInterpolatedHeight(", script)
        self.assertIn("if (TreesBlock(site, terrains))", script)
        self.assertIn("foreach (Terrain terrain in all)", script)
        self.assertIn("float.IsNaN(site.width)", script)
        self.assertIn("FootprintHalfExtents(site, ObstacleMargin", script)
        self.assertIn("FootprintHalfExtents(site, VegetationMargin", script)
        self.assertIn("float cos = Mathf.Abs(Mathf.Cos(yaw))", script)
        self.assertIn("float sin = Mathf.Abs(Mathf.Sin(yaw))", script)
        self.assertTrue((ROOT / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity").is_file())


if __name__ == "__main__":
    unittest.main(verbosity=2)
