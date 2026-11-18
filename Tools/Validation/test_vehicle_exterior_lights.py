#!/usr/bin/env python3
"""Source-level guards for low-overhead URP vehicle lights.

The Unity Editor and Android runtime must still validate shader variants,
visible FBX lamp alignment, actual headlight illumination and frame timing.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
MOTOR = (ROOT / "AarisVehicleMotor.cs").read_text("utf-8")
WORLD = (ROOT / "AarisVehicleWorld.cs").read_text("utf-8")


class VehicleExteriorLighting(unittest.TestCase):
    def test_lamps_follow_verified_aligned_chassis_not_prefab_axes(self):
        builder = WORLD.split("private bool Build(", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("motor.ConfigureExteriorLights(", builder)
        self.assertLess(builder.index("FaceEmbeddedVehicleForward("),
                        builder.index("motor.ConfigureExteriorLights("))
        self.assertLess(builder.index("motor.AttachEmbeddedWheelModels("),
                        builder.index("motor.ConfigureExteriorLights("))
        self.assertLess(builder.index("if (!wheelsAttached)"),
                        builder.index("motor.ConfigureExteriorLights("))
        self.assertIn("motor.FitShellAndWheelbase(bounds);", builder)
        self.assertIn("Bounds lampBounds = bounds;", builder)
        self.assertLess(builder.index("AlignEmbeddedTyresToRoad("),
                        builder.index("Bounds lampBounds = bounds;"))
        lights = MOTOR.split("public void ConfigureExteriorLights(", 1)[1].split(
            "private void RefreshExteriorLights()", 1)[0]
        self.assertIn("float width = fittedBounds.size.x;", lights)
        self.assertIn("float height = fittedBounds.size.y;", lights)
        self.assertIn("fittedBounds.max.z + .017f", lights)
        self.assertIn("fittedBounds.min.z - .017f", lights)
        self.assertIn("new Vector3(x, frontY, frontZ)", lights)
        self.assertIn("new Vector3(x, backY, backZ)", lights)
        self.assertIn("lampWidth * 1.20f", lights)
        self.assertIn("sharedLampLensMesh, lampBezelMaterial", builder)

    def test_never_add_interactive_collision_to_lamp_visuals(self):
        lenses = MOTOR.split("private static Renderer CreateLampLens(", 1)[1].split(
            "public void ConfigureExteriorLights(", 1)[0]
        # Now construct ONLY MeshFilter/MeshRenderer from the beginning.
        # No collider is ever created, so delayed Destroy is unnecessary.
        self.assertIn("new GameObject(label, typeof(MeshFilter),", lenses)
        self.assertIn("typeof(MeshRenderer)", lenses)
        self.assertIn("sharedMesh = sharedLensMesh;", lenses)
        self.assertNotIn("CreatePrimitive", lenses)
        self.assertNotIn("Collider", lenses)
        self.assertNotIn("Destroy(", lenses)
        self.assertIn("Quaternion.Euler(0f, 180f, 0f)", lenses)
        self.assertIn("renderer.shadowCastingMode =", lenses)
        self.assertIn("renderer.receiveShadows = false;", lenses)

    def test_shared_materials_and_cleanup(self):
        for label in (
            "headlampGlassMaterial", "headlampOnMaterial",
            "taillampGlassMaterial", "brakeLampMaterial", "reverseLampMaterial"
        ):
            self.assertIn(label + " = CreateLampMaterial(", WORLD)
            self.assertIn("Destroy(" + label + ");", WORLD)
        self.assertIn("lamp.EnableKeyword(\"_EMISSION\");", WORLD)
        self.assertIn("lamp.SetColor(\"_EmissionColor\", emission);", WORLD)
        self.assertIn("enableInstancing = true", WORLD)
        self.assertIn("sharedLampLensMesh = BuildLampLensMesh();", WORLD)
        self.assertIn("if (sharedLampLensMesh != null) Destroy(sharedLampLensMesh);", WORLD)
        self.assertIn("lampBezelMaterial = CreateLampMaterial(", WORLD)
        self.assertIn("if (lampBezelMaterial != null) Destroy(lampBezelMaterial);", WORLD)
        lights = MOTOR.split("private void RefreshExteriorLights()", 1)[1].split(
            "private void SyncVisibleWheels()", 1)[0]
        self.assertNotIn("new Material(", lights)
        self.assertNotIn("Instantiate(", lights)
        self.assertNotIn("GetComponent(", lights)
        self.assertNotIn("Physics.", lights)

    def test_headlight_only_while_driver_active(self):
        lights = MOTOR.split("private void RefreshExteriorLights()", 1)[1].split(
            "private void SyncVisibleWheels()", 1)[0]
        self.assertIn("driver != null && driver.isActiveAndEnabled &&", lights)
        self.assertIn("isActiveAndEnabled;", lights)
        self.assertIn("roadHeadlight.enabled = roadBeam;", lights)
        self.assertIn("bool roadBeam = driving && NeedsRoadBeam();", lights)
        self.assertIn("(roadBeam ? 8 : 0)", lights)
        configure = MOTOR.split("public void ConfigureExteriorLights(", 1)[1].split(
            "private void EnsureRoadHeadlight()", 1)[0]
        self.assertEqual(configure.count("AddComponent<Light>()"), 0)
        lamp = MOTOR.split("private void EnsureRoadHeadlight()", 1)[1].split(
            "private void RefreshExteriorLights()", 1)[0]
        self.assertIn("LightType.Spot", lamp)
        self.assertIn("LightShadows.None", lamp)
        self.assertIn("roadHeadlight.enabled = false;", lamp)
        self.assertEqual(lamp.count("AddComponent<Light>()"), 1)
        self.assertIn("EnsureRoadHeadlight();", MOTOR.split(
            "public bool TryTakeDriver(", 1)[1].split(
                "public void SetInput(", 1)[0])
        self.assertIn("if (lastLampState == lampState) return;", lights)

    def test_real_beam_uses_sunlight_sample_but_shared_lenses_remain(self):
        segment = MOTOR.split("private bool NeedsRoadBeam()", 1)[1].split(
            "private void RefreshExteriorLights()", 1)[0]
        for token in (
            "Time.unscaledTime < nextRoadBeamSampleAt",
            "Time.unscaledTime + 2f",
            "RenderSettings.sun",
            "sun == null || !sun.isActiveAndEnabled",
            "sun.type != LightType.Directional",
            "Vector3.Dot(-sun.transform.forward, Vector3.up)",
            "elevation < .18f || sun.intensity < .35f",
            "roadBeamNeeded = true;",
        ):
            self.assertIn(token, segment)
        for forbidden in ("Physics.", "new Material(", "Instantiate(", "GetComponent("):
            self.assertNotIn(forbidden, segment)
        # If there is no assigned sun, preserve the original nighttime beam.
        self.assertLess(segment.index("sun == null"), segment.index("return true;"))
        light = MOTOR.split("private void RefreshExteriorLights()", 1)[1].split(
            "private void SyncVisibleWheels()", 1)[0]
        self.assertIn("frontLamps[side].sharedMaterial =", light)
        self.assertIn("roadHeadlight.enabled = roadBeam;", light)

    def test_daylight_beam_thresholds_are_bounded(self):
        # A mathematical contract; Unity Editor must visually confirm the beam.
        needs_beam = lambda elevation, intensity: (
            elevation < .18 or intensity < .35
        )
        for sun_height, light_strength, expected in (
            (.80, 1.0, False), (.18, .35, False),
            (.179, .70, True), (.65, .349, True),
            (-.20, 1.0, True), (.9, 0.0, True),
        ):
            self.assertEqual(needs_beam(sun_height, light_strength), expected)

    def test_brake_and_reverse_follow_physics_and_clear_on_exit(self):
        lights = MOTOR.split("private void RefreshExteriorLights()", 1)[1].split(
            "private void SyncVisibleWheels()", 1)[0]
        self.assertIn("appliedBrakeTorque >= 500f", lights)
        self.assertIn("SpeedMetresPerSecond < .55f", lights)
        self.assertIn("forward < -.08f", lights)
        self.assertIn("reversingLamps[side].enabled = reversing;", lights)
        self.assertIn("rearLamps[side].sharedMaterial =", lights)
        self.assertIn("frontLamps[side].sharedMaterial =", lights)
        taking = MOTOR.split("public bool TryTakeDriver(", 1)[1].split(
            "public void SetInput(", 1)[0]
        self.assertIn("ApplyWheelForces(0f, 155f);", taking)
        self.assertIn("RefreshExteriorLights();", taking)
        release = MOTOR.split("public void ReleaseDriver(", 1)[1].split(
            "private void ApplyWheelForces(", 1)[0]
        self.assertIn("driver = null;", release)
        self.assertIn("RefreshExteriorLights();", release)
        disable = MOTOR.split("private void OnDisable()", 1)[1]
        self.assertIn("RefreshExteriorLights();", disable)
        fixed = MOTOR.split("private void FixedUpdate()", 1)[1].split(
            "private void ApplyAckermannSteering(", 1)[0]
        self.assertLess(fixed.index("ApplyWheelForces(drive, brake);"),
                        fixed.index("RefreshExteriorLights();"))
        self.assertIn("RefreshExteriorLights();", MOTOR.split(
            "private void OnEnable()", 1)[1].split(
            "public bool TryTakeDriver(", 1)[0])


if __name__ == "__main__":
    unittest.main()
