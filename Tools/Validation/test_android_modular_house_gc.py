#!/usr/bin/env python3
"""Android house spawning: no-allocation query reuse and visual parity contracts.

Static source checks only, not a Unity compile or device frame-time measurement.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisModularHouse.cs").read_text()
HOUSE = SOURCE.split("private static void CombineStaticSurfaces(", 1)[1].split(
    "public static GameObject Create(", 1)[0]


class AndroidModularHouseGc(unittest.TestCase):
    def test_unity_six_nonalloc_queries_replace_arrays(self):
        self.assertIn("root.GetComponentsInChildren<MeshFilter>(true, scratchFilters);", HOUSE)
        self.assertIn("renderer.GetSharedMaterials(scratchMaterials);", HOUSE)
        self.assertIn("scratchFilters.Clear();", HOUSE)
        self.assertIn("scratchMaterials.Clear();", HOUSE)
        self.assertNotIn("root.GetComponentsInChildren<MeshFilter>(true)", HOUSE)
        self.assertNotIn("renderer.sharedMaterials", HOUSE)

    def test_scratch_lists_are_pooled_and_references_cleared(self):
        self.assertIn("static readonly Dictionary<Material, List<CombineInstance>> scratchGroups", SOURCE)
        self.assertIn("static readonly List<List<CombineInstance>> scratchCombinePool", SOURCE)
        self.assertIn("group = scratchCombinePool[groupsUsed];", HOUSE)
        self.assertIn("scratchCombinePool.Add(group);", HOUSE)
        self.assertIn("group.Clear();", HOUSE)
        self.assertIn("finally", HOUSE)
        cleanup = HOUSE.split("finally", 1)[1]
        for line in (
            "group.Clear();", "groups.Clear();", "originals.Clear();",
            "scratchFilters.Clear();", "scratchMaterials.Clear();",
        ):
            self.assertIn(line, cleanup)

    def test_exact_sized_combination_buffers_reused_without_mesh_retention(self):
        self.assertIn("Dictionary<int, CombineInstance[]> scratchCombineArrays", SOURCE)
        self.assertIn("MaximumCachedCombineArraySizes = 24", SOURCE)
        self.assertIn("partCount <= 256", HOUSE)
        self.assertIn("new CombineInstance[partCount]", HOUSE)
        self.assertIn("scratchCombineArrays.TryGetValue(", HOUSE)
        self.assertIn("scratchCombineArrays.Add(partCount, parts);", HOUSE)
        self.assertIn("pair.Value.CopyTo(parts);", HOUSE)
        self.assertIn("combined.CombineMeshes(parts, true, true, false);", HOUSE)
        self.assertIn("if (cached) System.Array.Clear(parts, 0, parts.Length);", HOUSE)
        self.assertNotIn("pair.Value.ToArray()", HOUSE)
        self.assertLess(HOUSE.index("pair.Value.CopyTo(parts);"),
                        HOUSE.index("combined.CombineMeshes(parts, true, true, false);"))
        self.assertLess(HOUSE.index("combined.CombineMeshes(parts, true, true, false);"),
                        HOUSE.index("System.Array.Clear(parts, 0, parts.Length);"))

    def test_mesh_colliders_materials_and_visuals_preserved(self):
        for contract in (
            "source.isReadable",
            "filter.GetComponentInParent<AarisBreakablePanel>()",
            "source.subMeshCount != 1",
            "transform = toRoot * filter.transform.localToWorldMatrix",
            "if (vertices > 65534) return;",
            "combined.indexFormat = IndexFormat.UInt16;",
            "combined.CombineMeshes(parts, true, true, false);",
            "combined.RecalculateBounds();",
            "section.AddComponent<MeshFilter>().sharedMesh = combined;",
            "section.AddComponent<MeshRenderer>().sharedMaterial = pair.Key;",
            "combined.UploadMeshData(true);",
            "foreach (MeshRenderer original in originals) original.enabled = false;",
        ):
            with self.subTest(contract=contract):
                self.assertIn(contract, HOUSE)
        self.assertIn("CombineStaticSurfaces(t);", SOURCE)
        self.assertIn("PitchedRoof(t,width,depth,h);", SOURCE)
        self.assertIn("Front(t,width,depth,h,player,includePanels);", SOURCE)
        self.assertNotIn("QualitySettings.", HOUSE)
        self.assertNotIn("Destroy(", HOUSE)
        self.assertNotIn("GameObject.SetActive(", HOUSE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
