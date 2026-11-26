#!/usr/bin/env python3
"""Preprocessor regression for SpeedTree wind-buffer lifetime.

This checks the project HLSL's actual conditional branches, not Unity bytecode.
The native companion is AarisSpeedTreeVariantQA.Run in the licensed Editor.
"""
import itertools
from pathlib import Path
import re
import shutil
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[2]
PASSES = ROOT / "Assets/TerrainDemoScene_URP/ShaderGraphs/CustomFunctions/AarisSpeedTree8MobilePasses.hlsl"
WIND = ("_WINDQUALITY_NONE", "", "_WINDQUALITY_FAST", "_WINDQUALITY_BETTER",
        "_WINDQUALITY_BEST", "_WINDQUALITY_PALM")
PASS_DEFINES = ((), ("DEPTH_ONLY",), ("DEPTH_ONLY", "SHADOW_CASTER"), ("GBUFFER",))


def preprocess(source, defines):
    # Package includes belong to the native Unity test. Keep all project branch
    # conditions intact; an undefined package macro remains harmless HLSL text.
    source = re.sub(r'^\s*#include[^\n]*', '', source, flags=re.MULTILINE)
    command = [shutil.which("cpp"), "-P", "-undef", "-x", "c"]
    command.extend("-D" + define for define in defines if define)
    result = subprocess.run(command, input=source, text=True, capture_output=True,
                            check=True)
    return result.stdout


@unittest.skipUnless(shutil.which("cpp"), "C preprocessor required; native Unity gate remains separate")
class SpeedTreeWindVariantGuards(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = PASSES.read_text()

    def test_no_wind_variant_never_reads_undeclared_stwind_buffer(self):
        # URP SpeedTree8Input declares STWind only with ENABLE_WIND and without
        # _WINDQUALITY_NONE. Cover leaf/bark/billboard and depth/colour branches.
        for wind, enabled, leaf, billboard, pass_defines in itertools.product(
                WIND, (False, True), (False, True), (False, True), PASS_DEFINES):
            defines = [wind, *pass_defines]
            if enabled:
                defines.append("ENABLE_WIND")
            if leaf:
                defines.append("EFFECT_SUBSURFACE")
            if billboard:
                defines.append("EFFECT_BILLBOARD")
            if enabled and wind != "_WINDQUALITY_NONE":
                continue
            with self.subTest(defines=defines):
                processed = preprocess(self.source, defines)
                self.assertNotRegex(processed, r'\b_ST_Wind\w*\b')

    def test_none_keeps_local_and_macro_leaf_colour_with_neutral_exposure(self):
        processed = preprocess(self.source, ("ENABLE_WIND", "_WINDQUALITY_NONE",
                                              "EFFECT_SUBSURFACE"))
        self.assertIn("float crownExposure = 0.5;", processed)
        self.assertIn("leafSeed += dot(leafAnchorOS", processed)
        self.assertIn("float macroTone = AarisForestMacroTone(treePivotXZ);", processed)
        self.assertIn("leafToneMix += (crownExposure - 0.5) * 0.24;", processed)
        self.assertNotIn("crownExposure = saturate(", processed)

    def test_active_wind_and_existing_near_crown_cue_are_preserved(self):
        for wind in WIND[1:]:
            with self.subTest(wind=wind or "Fastest"):
                processed = preprocess(self.source, ("ENABLE_WIND", wind,
                                                      "EFFECT_SUBSURFACE"))
                self.assertIn("_ST_WindGlobal", processed)
                if wind:
                    self.assertIn("crownExposure = saturate(leafAnchorOS.y * inverseHeight);",
                                  processed)
                else:
                    # Fastest was already the distant, neutral-exposure path.
                    self.assertNotIn("crownExposure = saturate(", processed)


if __name__ == "__main__":
    unittest.main()
