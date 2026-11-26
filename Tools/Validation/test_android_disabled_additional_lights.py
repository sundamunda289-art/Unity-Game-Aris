from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
RUNTIME = (SCRIPTS / "AarisMobileRuntime.cs").read_text()
GUN = (SCRIPTS / "AarisEquippedGun.cs").read_text()
RIVAL = (SCRIPTS / "AarisRivalGunfireVfx.cs").read_text()
VEHICLE = (SCRIPTS / "AarisVehicleMotor.cs").read_text()
HIGH = (ROOT / "Assets/TerrainDemoScene_URP/Settings/URP/URP-HighQuality Pipeline Asset.asset").read_text()


class AndroidDisabledAdditionalLightsTests(unittest.TestCase):
    def test_saved_pipeline_explicitly_disables_additional_lights(self):
        self.assertIn("m_MainLightRenderingMode: 1", HIGH)
        self.assertIn("m_AdditionalLightsRenderingMode: 0", HIGH)

    def test_runtime_capability_is_android_only_and_future_safe(self):
        method = RUNTIME[RUNTIME.index("internal static bool CanRenderAdditionalLights()"):
                         RUNTIME.index("#if UNITY_EDITOR || DEVELOPMENT_BUILD")]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", method)
        self.assertIn("UniversalRenderPipeline.asset", method)
        self.assertIn("LightRenderingMode.Disabled", method)
        self.assertIn("asset == null ||", method)
        self.assertIn("return true;", method)

    def test_player_flash_keeps_mesh_visual_and_gates_only_native_light(self):
        ensure = GUN[GUN.index("private void EnsureMuzzleFlash()"):
                     GUN.index("private void TriggerMuzzleFlash()")]
        self.assertIn('new GameObject("Aaris Pooled Muzzle Flash"', ensure)
        self.assertIn("muzzleFlashRenderer.sharedMaterial = material;", ensure)
        gate = ensure.index("AarisMobileRuntime.CanRenderAdditionalLights()")
        light = ensure.index("lightObject.AddComponent<Light>()")
        self.assertLess(gate, light)
        self.assertIn("muzzleFlashRenderer.enabled = true;", GUN)

    def test_bot_flash_and_tracer_survive_without_light_component(self):
        create = RIVAL[RIVAL.index("private Slot CreateSlot()"):
                       RIVAL.index("private CasingSlot CreateCasingSlot()")]
        self.assertIn('new GameObject("Aaris Bot Reused Tracer")', create)
        self.assertIn('new GameObject("Aaris Bot Reused Tapered Barrel Fire"', create)
        self.assertLess(create.index("AarisMobileRuntime.CanRenderAdditionalLights()"),
                        create.index("light.AddComponent<Light>()"))
        self.assertIn("if (slot.burst != null)", RIVAL)
        self.assertIn("if (flash.burst != null)", RIVAL)
        self.assertIn("(slot.burst != null && slot.burst.enabled)", RIVAL)

    def test_vehicle_emissive_lamps_remain_and_spotlight_is_capability_gated(self):
        ensure = VEHICLE[VEHICLE.index("private void EnsureRoadHeadlight()"):
                         VEHICLE.index("private bool NeedsRoadBeam()")]
        self.assertLess(ensure.index("!AarisMobileRuntime.CanRenderAdditionalLights()"),
                        ensure.index("beamObject.AddComponent<Light>()"))
        refresh = VEHICLE[VEHICLE.index("private void RefreshExteriorLights()"):
                          VEHICLE.index("public void ConfigureBodyDynamics(")]
        self.assertIn("driving ? headlampOn : headlampGlass", refresh)
        self.assertIn("roadHeadlight != null && NeedsRoadBeam()", refresh)

    def test_combat_vfx_shader_resolution_uses_shared_cache(self):
        self.assertNotIn("Shader.Find(", GUN)
        self.assertNotIn("Shader.Find(", RIVAL)
        self.assertIn("AarisShaderCache.Find(", GUN)
        self.assertIn("AarisShaderCache.Find(", RIVAL)


if __name__ == "__main__":
    unittest.main()
