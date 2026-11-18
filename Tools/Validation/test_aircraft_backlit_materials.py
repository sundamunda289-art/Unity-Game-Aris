#!/usr/bin/env python3
"""Aircraft backlighting guard for the genuine URP/PBR transport materials."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
MATS = ROOT / "Assets/Resources/AarisDrop/Materials"

# Low-intensity neutral sky bounce, not a strong emissive or a real-time light.
EXTERIOR = (
    "CargoPlane-HulledMetal.mat",
    "CargoPlane-WingPanels.mat",
    "CargoPlane-SafetyAccent.mat",
)


class AircraftBacklitMaterials(unittest.TestCase):
    def test_original_urp_lit_shader_and_pbr_properties_retained(self):
        for name in EXTERIOR:
            with self.subTest(material=name):
                src = (MATS / name).read_text("utf-8")
                self.assertIn("m_Shader: {fileID: 4800000, guid: 933532a4fcc9baf4fa0491de14d08ed7, type: 3}", src)
                self.assertIn("- _NORMALMAP", src)
                self.assertIn("- _EMISSION", src)
                self.assertIn("- _Metallic:", src)
                self.assertIn("- _Smoothness:", src)
                self.assertIn("- _EnvironmentReflections: 1", src)
                self.assertIn("- _Surface: 0", src)
                self.assertIn("- _ReceiveShadows: 1", src)

    def test_fill_is_visible_but_not_cartoon_glow(self):
        for name in EXTERIOR:
            with self.subTest(material=name):
                src = (MATS / name).read_text("utf-8")
                match = re.search(
                    r"_EmissionColor: \\{r: ([0-9.]+), g: ([0-9.]+), b: ([0-9.]+), a: 1\\}",
                    src)
                self.assertIsNotNone(match)
                channels = [float(value) for value in match.groups()]
                for c in channels:
                    self.assertGreaterEqual(c, 0.035)
                    self.assertLessEqual(c, 0.095)
                self.assertLess(max(channels) - min(channels), 0.035)

    def test_glazing_and_engine_keep_intentional_contrast(self):
        for name in ("CargoPlane-DarkCockpit.mat", "CargoPlane-EngineDark.mat"):
            with self.subTest(material=name):
                src = (MATS / name).read_text("utf-8")
                self.assertIn("_EmissionColor: {r: 0, g: 0, b: 0, a: 1}", src)

    def test_aircraft_uses_existing_saved_finishes_not_new_lights(self):
        src = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineDropVisuals.cs").read_text("utf-8")
        self.assertIn('SavedAircraftFinish("CargoPlane-HulledMetal"', src)
        self.assertIn('SavedAircraftFinish("CargoPlane-WingPanels"', src)
        self.assertIn('SavedAircraftFinish("CargoPlane-SafetyAccent"', src)


if __name__ == "__main__":
    unittest.main(verbosity=2)
