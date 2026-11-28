#!/usr/bin/env python3
"""Source-level checks for low-cost interpolated coastal-water wave normals.

Actual frame-time improvement still requires Android GPU profiling.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SHADER = ROOT / "Assets/AarisWorld/Surfaces/CoastalWater.shader"
MESH = ROOT / "Assets/AarisWorld/Surfaces/Ocean.asset"


class MobileWaterNormalBudgetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SHADER.read_text(encoding="utf-8")

    def test_wave_normals_are_interpolated_not_trig_per_fragment(self):
        source = self.source
        vertex = source.split("WaterVaryings WaterVertex(", 1)[1].split(
            "ENDHLSL", 1
        )[0]
        forward = source.split("half4 WaterFragment(", 1)[1]
        self.assertIn("half3 normalWS : TEXCOORD2;", source)
        self.assertIn("output.normalWS = WaterNormalWS(output.positionWS);", vertex)
        self.assertIn(
            "lighting.normalWS = normalize(input.normalWS + half3(ripple.x, 0, ripple.y));",
            forward,
        )
        self.assertIn("_RippleStrength * rippleVisibility", forward)
        self.assertNotIn("WaterNormalWS(", forward)
        self.assertNotIn("cos(", forward)
        self.assertLess(
            source.index("half3 WaterNormalWS("),
            source.index("WaterVaryings WaterVertex("),
        )

    def test_original_wave_displacement_and_pbr_are_preserved(self):
        source = self.source
        for token in (
            "0.10 * sin(p.x * 0.045 + _Time.y * 0.6)",
            "0.06 * sin(p.z * 0.063 - _Time.y * 0.45)",
            "UniversalFragmentPBR(lighting, water)",
            "MixFog(result.rgb, input.fogFactor)",
            'Name "DepthOnly"',
            'Name "ForwardLit"',
        ):
            with self.subTest(token=token):
                self.assertIn(token, source)

    def test_existing_mesh_kept_intact(self):
        mesh = MESH.read_text(encoding="utf-8")
        self.assertIn("m_VertexCount: 16641", mesh)
        self.assertIn("indexCount: 98304", mesh)


if __name__ == "__main__":
    unittest.main(verbosity=2)
