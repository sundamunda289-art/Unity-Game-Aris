#!/usr/bin/env python3
"""Source-level contracts for Android expansion Resources prewarm."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / "Assets/AarisWorld/Scripts/AarisSettlementExpansion.cs").read_text()


class ExpansionCatalogPrewarm(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.start = SRC.split("private void Start()", 1)[1].split(
            "private void InitializeExpansion(", 1
        )[0]
        cls.init = SRC.split("private void InitializeExpansion(", 1)[1].split(
            "private IEnumerator PlaceSitesAcrossFrames(", 1
        )[0]

    def test_android_uses_ordered_async_resources(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", self.start)
        self.assertIn("StartCoroutine(LoadExpansionCatalogAcrossFrames());", self.start)
        self.assertIn('Resources.LoadAsync<TextAsset>(', self.start)
        self.assertIn('Resources.LoadAsync<AarisExpansionCatalog>(', self.start)
        self.assertIn('"AarisExpansion/WorldExpansionPlan"', self.start)
        self.assertIn('"AarisExpansion/AarisExpansionCatalog"', self.start)
        self.assertLess(self.start.index("yield return plan;"),
                        self.start.index("Resources.LoadAsync<AarisExpansionCatalog>"))
        self.assertLess(self.start.index("yield return catalog;"),
                        self.start.index("InitializeExpansion(plan.asset as TextAsset,"))
        self.assertIn("plan.priority = -1;", self.start)
        self.assertIn("catalog.priority = -1;", self.start)
        self.assertIn("if (!isActiveAndEnabled) yield break;", self.start)
        self.assertGreaterEqual(self.start.count("yield return null;"), 2)
        self.assertNotIn("Instantiate(", self.start)

    def test_editor_preserves_original_synchronous_resource_behavior(self):
        desktop = self.start.split("#else", 1)[1].split("#endif", 1)[0]
        self.assertIn('Resources.Load<TextAsset>("AarisExpansion/WorldExpansionPlan")', desktop)
        self.assertIn('Resources.Load<AarisExpansionCatalog>("AarisExpansion/AarisExpansionCatalog")',
                      desktop)
        self.assertIn("InitializeExpansion(", desktop)

    def test_catalog_is_validated_before_any_placement(self):
        self.assertIn("if (planAsset == null || catalog == null)", self.init)
        self.assertIn("AARIS_EXPANSION_LOCAL_CATALOG_MISSING", self.init)
        self.assertIn("AARIS_EXPANSION_INVALID_PLAN_JSON", self.init)
        self.assertIn("AARIS_EXPANSION_SCHEMA_OR_WORLD_MISMATCH", self.init)
        self.assertIn("plan.schemaVersion != 1", self.init)
        self.assertIn("plan.mapSizeMetres != 2000", self.init)
        self.assertIn("plan.targetGameScene != Scene", self.init)
        self.assertIn("plan.sites.Length > 32", self.init)
        self.assertIn("AARIS_EXPANSION_FOUR_TERRAIN_TILES_REQUIRED", self.init)
        self.assertIn("StartCoroutine(PlaceSitesAcrossFrames(plan, catalog, terrains, interactionTarget));",
                      self.init)

    def test_site_order_native_tree_and_gameplay_geometry_preserved(self):
        self.assertIn("AndroidTreeProbeBudgetPerFrame = 256", SRC)
        self.assertIn("if (mobile && (++processed >= AndroidTreeProbeBudgetPerFrame ||", SRC)
        self.assertIn("foreach (var site in plan.sites)", SRC)
        self.assertIn("int attemptsPerFrame = mobile ? 1 : 2;", SRC)
        self.assertIn("!TryRelocateInteractiveSite(site, terrains, out y)", SRC)
        self.assertIn('catalog.ResolveOpen(site.prefabKey)', SRC)
        self.assertIn('catalog.Resolve(site.prefabKey)', SRC)
        self.assertIn("AarisModularHouse.Create(position, rotation, site.width,", SRC)
        self.assertIn("Instantiate(prefab, position, rotation, transform);", SRC)
        self.assertIn("treeCache.Clear();", SRC)
        self.assertIn("private const float SeaLevel = -38.07552f;", SRC)


if __name__ == "__main__":
    unittest.main()
