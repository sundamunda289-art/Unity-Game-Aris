from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
GLOBAL = (ROOT / "Assets/TerrainDemoScene_URP/Settings/URP/UniversalRenderPipelineGlobalSettings.asset").read_text()
RENDERER = (ROOT / "Assets/TerrainDemoScene_URP/Settings/URP/Universal Render Pipeline Asset_Renderer.asset").read_text()
HIGH = (ROOT / "Assets/TerrainDemoScene_URP/Settings/URP/URP-HighQuality Pipeline Asset.asset").read_text()
SCRIPTS = ROOT / "Assets/AarisWorld"


class Urp17RenderGraphMobileTests(unittest.TestCase):
    def test_rendergraph_enabled_and_compatibility_path_disabled(self):
        self.assertIn("m_EnableRenderGraph: 1", GLOBAL)
        self.assertIn("m_EnableRenderCompatibilityMode: 0", GLOBAL)
        self.assertIn("m_EnableCompilationCaching: 1", GLOBAL)
        self.assertIn("m_EnableValidityChecks: 1", GLOBAL)

    def test_forward_renderer_and_store_actions_remain_mobile_friendly(self):
        self.assertIn("m_RenderingMode: 0", RENDERER)
        self.assertIn("m_RendererFeatures: []", RENDERER)
        self.assertIn("m_StoreActionsOptimization: 0", HIGH)

    def test_quality_sensitive_camera_inputs_are_untouched(self):
        self.assertIn("m_RequireDepthTexture: 1", HIGH)
        self.assertIn("m_RequireOpaqueTexture: 1", HIGH)
        self.assertIn("m_SupportsHDR: 1", HIGH)
        self.assertIn("m_MSAA: 4", HIGH)

    def test_no_project_owned_legacy_render_pass_dependency(self):
        forbidden = (
            "ScriptableRenderPass",
            "ScriptableRendererFeature",
            "CommandBuffer.Blit",
            "RenderingUtils.Blit",
        )
        for path in SCRIPTS.rglob("*.cs"):
            code = path.read_text(errors="ignore")
            for token in forbidden:
                with self.subTest(file=str(path.relative_to(ROOT)), token=token):
                    self.assertNotIn(token, code)


if __name__ == "__main__":
    unittest.main()
