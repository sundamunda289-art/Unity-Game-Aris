#!/usr/bin/env python3
"""Parachute first-use native work: source contract, not actual Android timing."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
S = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineDropVisuals.cs").read_text()
DROP = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineDrop.cs").read_text()

def section(start, end):
    return S.split(start, 1)[1].split(end, 1)[0]

class CanopyNativeBudget(unittest.TestCase):
    def test_original_builtin_cylinder_geometry_is_reused(self):
        helper = section("private static Mesh EnsureCanopyCordMesh()", "private static void RigidLine(")
        self.assertIn("GameObject.CreatePrimitive(PrimitiveType.Cylinder)", helper)
        self.assertIn("prototype.SetActive(false);", helper)
        self.assertIn("prototype.GetComponent<MeshFilter>().sharedMesh", helper)
        self.assertIn("if (cachedCanopyCordMesh != null) return cachedCanopyCordMesh;", helper)
        self.assertIn("UnityEngine.Object.Destroy(prototype);", helper)
    def test_every_rope_is_renderer_only_without_ghost_colliders(self):
        rope = section("private static void RigidLine(", "private static Mesh BuildPropellerMesh(")
        self.assertIn('new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer))', rope)
        self.assertIn("EnsureCanopyCordMesh()", rope)
        self.assertIn("new Vector3(diameter, offset.magnitude * 0.5f, diameter)", rope)
        self.assertIn("Quaternion.FromToRotation(Vector3.up, offset)", rope)
        self.assertIn("line.GetComponent<MeshRenderer>().sharedMaterial = material;", rope)
        for forbidden in ("GameObject.CreatePrimitive", "AddComponent<Collider>",
                          "typeof(Collider)", "new Mesh", "Object.Destroy("):
            self.assertNotIn(forbidden, rope)
    def test_identical_four_canopy_colors_prewarmed_after_aircraft(self):
        pre = section("internal static IEnumerator PrewarmCanopyPrefab()", "#endif")
        for expected in (
            "Paint(ref canopyBlue, new Color(0.05f, 0.23f, 0.39f));",
            "Paint(ref canopyWhite, new Color(0.91f, 0.91f, 0.83f));",
            "Paint(ref canopyOrange, new Color(0.97f, 0.45f, 0.13f));",
            "Paint(ref rope, new Color(0.09f, 0.12f, 0.15f));",
            "EnsureCanopyCordMesh();"):
            self.assertIn(expected, pre)
        self.assertGreaterEqual(pre.count("yield return null;"), 6)
        self.assertIn("request.priority = -1;", pre)
        self.assertIn("yield return request;", pre)
        self.assertLess(pre.index("yield return request;"), pre.index("Paint(ref canopyBlue"))
        self.assertNotIn("new Material(", pre)
        self.assertNotIn("new GameObject(", pre)
    def test_open_logic_unchanged_and_original_mesh_fallback_kept(self):
        art = section("internal static GameObject CreateElongatedCanopy(", "private static Vector3 CanopyPoint(")
        for original in (
            "prewarmedCanopyPrefab != null",
            'Resources.Load<GameObject>("AarisDrop/ElongatedParachute")',
            "var model = UnityEngine.Object.Instantiate(imported, rig, false);",
            'model.name = "LocalCanopyArt";',
            'else\n            {\n                Material[] paints',
            "RigidLine(rig,"):
            self.assertIn(original, art)
        self.assertIn("canopy = AarisOfflineDropVisuals.CreateElongatedCanopy(transform);", DROP)
    def test_static_state_resets_without_touching_saved_assets(self):
        reset = section("private static void ResetCanopyPrewarm()", "internal static IEnumerator PrewarmAircraftFinishes()")
        for token in ("prewarmedCanopyPrefab = null;",
                      "canopyVisualsPrewarmed = false;",
                      "cachedCanopyCordMesh = null;",
                      "aircraftFinishCache.Clear();"):
            self.assertIn(token, reset)
        self.assertIn("if (canopyVisualsPrewarmed) yield break;", S)

if __name__ == "__main__":
    unittest.main(verbosity=2)
