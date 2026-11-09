#!/usr/bin/env python3
"""Static contract guard for one-time Editor native deep forest bake."""
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[2]
BAKER=ROOT/"Assets/AarisWorld/Editor/AarisDeepForestNativeBake.cs"
VARIANT=ROOT/"Assets/TerrainDemoScene_URP/Prefabs/Trees/DeepForest/DeepForestPine.prefab"

class NativeDeepForestBakeGuard(unittest.TestCase):
    def test_asset_references_are_native_not_runtime(self):
        source=BAKER.read_text("utf-8")
        self.assertTrue(VARIANT.exists())
        self.assertIn("Aaris/CI/Bake bounded native deep forest variant",source)
        self.assertIn("EditorSceneManager.OpenScene(ScenePath,OpenSceneMode.Single)",source)
        self.assertIn("AssetDatabase.LoadAssetAtPath<GameObject>(TreePath)",source)
        self.assertIn("plan.data.treePrototypes=plan.prototypes;",source)
        self.assertIn("plan.data.SetTreeInstances(plan.instances,false);",source)
        self.assertIn("AssetDatabase.SaveAssets();",source)
        self.assertIn("MaxConversions = 48",source)
        self.assertIn("result.oldTrees!=result.newTrees",source)
        self.assertIn("terrain.transform.position+",source)
        self.assertIn("if(!InsideForest(world) || PositionHash(world)%5u!=0u)continue;",source)
        self.assertNotIn("Instantiate(",source)
        self.assertNotIn("Update()",source)
        self.assertNotIn("NetworkBehaviour",source)
        self.assertNotIn("Object.Destroy(",source)
        self.assertNotIn("git reset",source)

if __name__=="__main__":
    unittest.main(verbosity=2)
