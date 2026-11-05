"""Read-only Unity Editor garage physics probe source contract.

A C# source parser is *not* a Unity import, scene collider execution, or
vehicle driving test; this catches dangerous accidental writes at PR review.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
SOURCE = ROOT / "Assets/AarisWorld/Editor/AarisResidentialRoutePhysicsAudit.cs"
META = ROOT / "Assets/AarisWorld/Editor/AarisResidentialRoutePhysicsAudit.cs.meta"


class ResidentialPhysicsEditorSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.code = SOURCE.read_text()
        cls.meta = META.read_text()

    def test_native_probe_matches_all_17_reviewed_garages(self):
        self.assertIn("Physics.OverlapBoxNonAlloc(", self.code)
        self.assertIn("Physics.SyncTransforms()", self.code)
        self.assertIn("Terrain.activeTerrains.Length != 4", self.code)
        self.assertIn("layout.vehicle_spawns.Length != 45", self.code)
        self.assertIn("manifest.rows.Length != 17", self.code)
        self.assertIn("Vector3.Distance(position,", self.code)
        self.assertIn("MinRoadLink = 5.0f", self.code)
        self.assertIn("GroundClearance = 0.15f", self.code)
        self.assertIn("t.terrainData.GetInterpolatedHeight(u, v)", self.code)
        self.assertNotIn("t.SampleHeight(", self.code)
        self.assertIn("AARIS_EDITOR_GARAGE_ROUTE_PHYSICS_AUDIT=", self.code)
        self.assertIn("QueryTriggerInteraction.Ignore", self.code)

    def test_editor_probe_is_read_only_and_has_stable_new_identity(self):
        for forbidden in (
            "EditorSceneManager.Save", "AssetDatabase.Save",
            "File.Write", "File.Delete", "DestroyImmediate",
            "PrefabUtility.Save", "EditorSceneManager.OpenScene",
            "transform.position =", "Transform.SetPositionAndRotation",
            "Rigidbody.AddForce", "SceneManager.LoadScene",
        ):
            self.assertNotIn(forbidden, self.code)
        self.assertIn("SceneManager.GetActiveScene().path != ScenePath", self.code)
        self.assertIn("if (collider == null || collider is TerrainCollider ||", self.code)
        guid = re.search(r"^guid:\s*([a-f0-9]{32})$",self.meta,re.M)
        self.assertIsNotNone(guid)
        self.assertEqual(guid.group(1),"cc1dcd8218964cdcb92e8f2e024d936a")

    def test_cpp_like_source_is_syntactically_valid_csharp_if_parser_available(self):
        try:
            from tree_sitter import Language, Parser
            from tree_sitter_c_sharp import language
        except ImportError:
            self.skipTest("Static C# parser not installed; Unity is still required")
        tree = Parser(Language(language())).parse(self.code.encode("utf-8"))
        self.assertFalse(tree.root_node.has_error,
                         "Native Unity Editor C# source has a syntax error")


if __name__=="__main__":
    unittest.main()
