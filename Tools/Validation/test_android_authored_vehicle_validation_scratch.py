#!/usr/bin/env python3
"""Check allocation-reuse semantics and original car geometry contracts."""
import unittest
from pathlib import Path

SRC=(Path(__file__).resolve().parents[2]/
     "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text("utf8")


class AuthoredVehicleValidationScratch(unittest.TestCase):
    def test_wheel_catalogue_has_no_array_or_lowercase_per_node(self):
        body=SRC.split("private static bool HasEmbeddedWheelSet(GameObject source)",1)[1].split(
            "private Material AuthoredUrpMaterial(",1)[0]
        self.assertIn("source.GetComponentsInChildren<Transform>(true, all);",body)
        for token in ("List<Transform> all = namedWheelQueryScratch;",
                      "StringComparison.OrdinalIgnoreCase",
                      'label.IndexOf("wheel_fl",',
                      'label.IndexOf("wheel_fr",',
                      'label.IndexOf("wheel_rl",',
                      'label.IndexOf("wheel_rr",',
                      "finally", "all.Clear();", "if (fl && fr && rl && rr) return true;"):
            self.assertIn(token,body)
        self.assertNotIn("ToLowerInvariant()",body)
        self.assertNotIn("source.GetComponentsInChildren<Transform>(true)",body)
        self.assertNotIn("new Transform[",body)

    def test_lamp_mesh_uses_same_existing_wheel_lookup_scratch(self):
        body=SRC.split("private static bool TryGetAuthoredLampBodyBounds(",1)[1].split(
            "private static Transform FindNamedWheel(",1)[0]
        self.assertIn("List<Transform> all = namedWheelQueryScratch;",body)
        self.assertIn("visual.GetComponentsInChildren<Transform>(true, all);",body)
        self.assertIn("finally",body)
        self.assertGreaterEqual(body.count("all.Clear();"),2)
        self.assertNotIn("visual.GetComponentsInChildren<Transform>(true)",body)
        for token in ('child.name.EndsWith("_body",',
                      "StringComparison.OrdinalIgnoreCase",
                      "child.GetComponent<MeshFilter>() == null",
                      "TryGetVisualBoundsLocal(chassis, child, out Bounds candidate)",
                      "candidate.size.x < wholeCar.size.x * .55f",
                      "candidate.size.z < wholeCar.size.z * .68f",
                      "candidate.size.z > wholeCar.size.z * 1.04f",
                      "candidate.size.y < .25f",
                      "bodyOnly = candidate;"):
            self.assertIn(token,body)

    def test_original_wheel_name_method_and_graphics_unchanged(self):
        self.assertEqual(SRC.count(
            "[ThreadStatic] private static List<Transform> namedWheelQueryScratch;"),1)
        self.assertIn("root.GetComponentsInChildren<Transform>(true, all);",SRC)
        self.assertIn("try\n            {",SRC)
        self.assertIn("AccumulateLocalBounds(filter.sharedMesh.bounds,",SRC)
        self.assertIn("HasEmbeddedWheelSet(authoredBody)",SRC)
        self.assertIn("TryGetAuthoredLampBodyBounds(root.transform, mesh.transform,",SRC)


if __name__=="__main__":
    unittest.main(verbosity=2)
