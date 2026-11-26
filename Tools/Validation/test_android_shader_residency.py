from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"
RUNTIME = (SCRIPTS / "AarisMobileShaderResidency.cs").read_text()

WORLD_SHADER_USERS = (
    "AarisCharacterVisual.cs",
    "AarisHouseFacadeTrim.cs",
    "AarisMobileRuntime.cs",
    "AarisModularHouse.cs",
    "AarisOfflineBRDirector.cs",
    "AarisOfflineDropVisuals.cs",
    "AarisRoadsideBikeScenery.cs",
    "AarisVehicleWorld.cs",
)


class AndroidShaderResidencyTests(unittest.TestCase):
    def test_chunk_residency_is_android_only_early_and_quality_neutral(self):
        self.assertIn("RuntimeInitializeLoadType.BeforeSplashScreen", RUNTIME)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", RUNTIME)
        self.assertIn("AarisMobileRuntime.UseConstrainedGpuStreamingBudget()", RUNTIME)
        self.assertIn("Shader.maximumChunksOverride", RUNTIME)
        self.assertIn("if (ramMB > 6144) return;", RUNTIME)
        self.assertIn("ramMB > 0 && ramMB <= 4096 ? 4 : 6", RUNTIME)
        for forbidden in (
            "Shader.globalMaximumLOD",
            "renderScale",
            "QualitySettings.SetQualityLevel",
            "TerrainData",
            "SetShaderPassEnabled",
        ):
            self.assertNotIn(forbidden, RUNTIME)

    def test_lookup_cache_does_not_make_missing_shader_permanently_missing(self):
        find = RUNTIME[RUNTIME.index("internal static Shader Find("):
                       RUNTIME.index("internal sealed class AarisMobileShaderResidency")]
        self.assertIn("Shader resolved = Shader.Find(shaderName);", find)
        self.assertIn("if (resolved != null)", find)
        self.assertNotIn("shaders[shaderName] = null", find)

    def test_common_resolve_is_spread_across_global_frame_gate(self):
        start = RUNTIME[RUNTIME.index("private IEnumerator Start()"):]
        self.assertIn("yield return null;\n            yield return null;", start)
        self.assertIn("AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()", start)
        self.assertIn("AarisShaderCache.Find(CommonRuntimeShaders[i]);", start)
        self.assertNotIn("Shader.WarmupAllShaders", RUNTIME)
        self.assertNotIn("WarmUp()", RUNTIME)

    def test_world_runtime_shader_lookups_use_shared_cache(self):
        for name in WORLD_SHADER_USERS:
            code = (SCRIPTS / name).read_text()
            with self.subTest(file=name):
                self.assertNotIn("Shader.Find(", code)
                self.assertIn("AarisShaderCache.Find(", code)


if __name__ == "__main__":
    unittest.main()
