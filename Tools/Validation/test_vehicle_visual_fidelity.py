#!/usr/bin/env python3
"""Visual-fidelity guards for real authored car art, shared chamfered
lights, and non-destructive damage tint. Unity/Android renders still required.
"""
import hashlib
import math
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
WORLD = (SCRIPTS / "AarisVehicleWorld.cs").read_text("utf-8")
MOTOR = (SCRIPTS / "AarisVehicleMotor.cs").read_text("utf-8")
AUTHORED = ROOT / "Assets/Resources/AarisVehicles/Authored"

EXPECTED_CC0_FBX = {
    "1980s-van": "c50f781d642e39f84cbe664ea2ec7c1545d33e173c229c17031fd0464e5ba285",
    "1990s-hatchback": "9266b0ac0bbccee09cb03a3ade7a17483d641fbe8d4baffbf6fd6bcb0271c932",
    "1990s-pickup-truck": "a561a47fe2b92404a7a35d32be209d4d618a6b5e9475e70cfa4dc1394fc6ab5d",
    "1990s-sports-car": "42eefdcde7275013aa5894e577c81a92e991f042750ff4c7360ef71764dcf826",
    "1990s-suv": "64069497f9c436ef8c2d8b197c51e8abf8c9f9656bedfe6834f04c9996673f1b",
}


class VehicleVisualFidelity(unittest.TestCase):
    def test_real_vendor_fbx_and_embedded_axles_not_changed(self):
        for name, checksum in EXPECTED_CC0_FBX.items():
            with self.subTest(name=name):
                body = AUTHORED / (name + ".fbx")
                raw = body.read_bytes()
                self.assertGreater(len(raw), 1_900_000)
                self.assertEqual(hashlib.sha256(raw).hexdigest(), checksum)
                for axle in ("wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr"):
                    self.assertIn(axle.encode("ascii"), raw)
                metadata = (AUTHORED / (name + ".fbx.meta")).read_text()
                self.assertIn(name + "_body_pbr", metadata)
                self.assertIn(name + "_tire_x", metadata)
                material = AUTHORED / ("PBR_" + name) / (name + "_body_pbr.mat")
                text = material.read_text()
                for key in ("_BaseMap:", "_BumpMap:", "_MetallicGlossMap:"):
                    self.assertIn(key, text)
                self.assertIn("m_EnableInstancingVariants: 1", text)

    def test_one_shared_8_corner_lens_without_cube_colliders(self):
        source = WORLD.split("private static Mesh BuildLampLensMesh()", 1)[1].split(
            "// URP Lit is already validated", 1)[0]
        self.assertIn("new Mesh", source)
        self.assertEqual(source.split("lens.vertices", 1)[1].split(
            "lens.triangles", 1)[0].count("new Vector3("), 8)
        triangles = source.split("lens.triangles = new[]", 1)[1].split("};", 1)[0]
        indices = list(map(int, re.findall(r"\b\d+\b", triangles)))
        self.assertEqual(len(indices), 18)
        self.assertTrue(all(0 <= i < 8 for i in indices))
        self.assertIn("lens.RecalculateNormals();", source)
        self.assertIn("lens.RecalculateBounds();", source)
        renderer = MOTOR.split("private static Renderer CreateLampLens(", 1)[1].split(
            "public void ConfigureExteriorLights(", 1)[0]
        self.assertIn("typeof(MeshFilter)", renderer)
        self.assertIn("typeof(MeshRenderer)", renderer)
        self.assertIn("sharedMesh = sharedLensMesh;", renderer)
        self.assertIn("renderer.sharedMaterial = material;", renderer)
        self.assertIn("rearFacing", renderer)
        for forbidden in ("CreatePrimitive(", "Collider", "new Mesh(", "new Material("):
            self.assertNotIn(forbidden, renderer)
        self.assertIn("sharedLampLensMesh = BuildLampLensMesh();", WORLD)
        self.assertIn("if (sharedLampLensMesh != null) Destroy(sharedLampLensMesh);", WORLD)

    def test_lamp_clusters_scale_to_six_body_types_and_no_shadow(self):
        cfg = MOTOR.split("public void ConfigureExteriorLights(", 1)[1].split(
            "private void EnsureRoadHeadlight()", 1)[0]
        for piece in (
            "float halfTrack = Mathf.Clamp(width * .385f, .48f, .84f)",
            "fittedBounds.max.z + .017f",
            "fittedBounds.min.z - .017f",
            "lampWidth * 1.20f",
            "lampHeight * 1.32f",
            "Headlight Bezel", "Taillight Bezel",
            "Beveled Headlamp", "Beveled Brake Lamp",
            "Recessed Reverse Lamp"
        ):
            self.assertIn(piece, cfg)
        self.assertEqual(cfg.count("AddComponent<Light>()"), 0)
        lazy = MOTOR.split("private void EnsureRoadHeadlight()", 1)[1].split(
            "private void RefreshExteriorLights()", 1)[0]
        self.assertIn("LightShadows.None", lazy)
        self.assertEqual(lazy.count("AddComponent<Light>()"), 1)
        self.assertIn("roadHeadlight.enabled = false;", lazy)
        for width, height, length in (
            (1.8, 1.5, 4.1), (1.9, 1.2, 4.4), (2.05, 1.93, 4.9),
            (1.9, 1.9, 4.7), (2.0, 1.9, 5.3), (1.85, 1.35, 4.2)
        ):
            half_track = min(.84, max(.48, width * .385))
            lamp_w = min(.39, max(.26, width * .174))
            lamp_h = min(.17, max(.10, height * .097))
            self.assertGreater(half_track - lamp_w * .5, 0)
            self.assertLess(half_track + lamp_w * .6, width * .5 + .1)
            self.assertGreater(lamp_h, .09)
            self.assertGreater(length, 3.5)

    def test_suv_spare_wheel_does_not_move_rear_lamps_into_air(self):
        helper = WORLD.split("private static bool TryGetAuthoredLampBodyBounds(", 1)[1].split(
            "private static Transform FindNamedWheel(", 1)[0]
        self.assertIn('child.name.EndsWith("_body",', helper)
        self.assertIn("child.GetComponent<MeshFilter>() == null", helper)
        self.assertIn("TryGetVisualBoundsLocal(chassis, child, out Bounds candidate)", helper)
        self.assertIn("candidate.size.z < wholeCar.size.z * .68f", helper)
        self.assertIn("candidate.size.z > wholeCar.size.z * 1.04f", helper)
        builder = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("Bounds lampBounds = bounds;", builder)
        self.assertIn("motor.FitShellAndWheelbase(bounds);", builder)
        self.assertIn("record.type.useEmbeddedAuthoredWheels &&", builder)
        self.assertIn("TryGetAuthoredLampBodyBounds(root.transform, mesh.transform,", builder)
        self.assertIn("lampBounds = bodyOnly;", builder)
        self.assertIn("motor.ConfigureExteriorLights(lampBounds,", builder)
        self.assertLess(builder.index("if (!wheelsAttached)"),
                        builder.index("TryGetAuthoredLampBodyBounds("))
        self.assertLess(builder.index("TryGetAuthoredLampBodyBounds("),
                        builder.index("motor.ConfigureExteriorLights(lampBounds,"))

    def test_wear_applies_only_to_instance_pbr_paint_slots(self):
        method = MOTOR.split("public void ConfigureBodyWear(", 1)[1].split(
            "public void ConfigureRearTyreTraces(", 1)[0]
        self.assertIn('label.IndexOf("body_pbr"', method)
        self.assertIn('label.IndexOf("AarisPaint"', method)
        self.assertIn('label.IndexOf("AarisCivilianSurface"', method)
        self.assertIn("if (!separate && !atlas && !sedan) continue;", method)
        self.assertIn("Color fresh = AarisVehicleWorld.SelectPaintTint(", method)
        helper = WORLD.split("internal static Color SelectPaintTint(", 1)[1].split(
            "private static void PaintVerifiedBodySlots(", 1)[0]
        self.assertIn('material.GetColor("_BaseColor")', helper)
        self.assertIn("original.r * paintFactor.r", helper)
        self.assertIn("fresh = fresh, atlas = atlas", method)
        self.assertIn("entry.atlas ? .73f : .52f", method)
        self.assertIn('entry.renderer.GetPropertyBlock(bodyPaintBlock, entry.index);', method)
        self.assertIn('bodyPaintBlock.SetColor("_BaseColor", worn);', method)
        self.assertIn("entry.renderer.SetPropertyBlock(bodyPaintBlock, entry.index);", method)
        self.assertIn("Mathf.Abs(lastBodyWear - damage01) < .015f", method)
        self.assertNotIn("renderer.material", method)
        self.assertNotIn("new Material(", method)
        self.assertNotIn("new Mesh(", method)
        collision = MOTOR.split("private void OnCollisionEnter(", 1)[1].split(
            "public void Configure(", 1)[0]
        self.assertIn("RefreshBodyWear();", collision)
        builder = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("motor.RestoreStreamedDamage(record.damage01);", builder)
        self.assertIn("motor.ConfigureBodyWear(mesh.transform, BodyPaintColours[", builder)
        self.assertLess(builder.index("PaintVerifiedBodySlots(mesh, record);"),
                        builder.index("motor.ConfigureBodyWear("))
        self.assertLess(builder.index("motor.RestoreStreamedDamage("),
                        builder.index("motor.ConfigureBodyWear("))


if __name__ == "__main__":
    unittest.main()
