#!/usr/bin/env python3
"""Verify authored cargo-transport material order and cheap daylight fill."""
import os
from pathlib import Path
import re
import unittest

ROOT = Path(os.environ.get("AARIS_TEST_ROOT", str(Path(__file__).resolve().parents[2])))
CS = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineDropVisuals.cs").read_text()
MODEL = ROOT / "Assets/Resources/AarisDrop/CargoPlaneRealistic.obj"
MATERIALS = ROOT / "Assets/Resources/AarisDrop/Materials"


class CargoPlaneVisualContract(unittest.TestCase):
    def test_stripped_mtl_slot_order_matches_actual_cc0_obj(self):
        seen = []
        with MODEL.open(errors="replace") as obj:
            for line in obj:
                if line.startswith("usemtl "):
                    name = line[7:].strip()
                    if name not in seen:
                        seen.append(name)
                    if len(seen) == 5:
                        break
        self.assertEqual(seen, ["WingPanels", "EngineDark", "HulledMetal",
                                "DarkCockpit", "SafetyAccent"])
        code = CS.split("else if (slots.Length == 5)", 1)[1].split(
            "else slots[i] = hullPaint;", 1)[0]
        for branch in ("i == 0 ? wingPaint", "i == 1 ? enginePaint",
                       "i == 2 ? hullPaint", "i == 3 ? cockpitPaint",
                       ": safetyPaint;"):
            self.assertIn(branch, code)

    def test_daylight_fill_uses_cached_lit_materials_without_new_lights(self):
        for name in ("HulledMetal", "WingPanels"):
            material = (MATERIALS / ("CargoPlane-" + name + ".mat")).read_text()
            # Unity import may remove this serialized keyword. Runtime code
            # must enable it on a cached copy regardless of serialized state.
            self.assertIn("m_ValidKeywords:", material)
            self.assertIn("m_LightmapFlags: 0", material)
            emission_line = next(line for line in material.splitlines()
                                 if line.strip().startswith("- _EmissionColor:"))
            values = re.findall(r"[rgb]: ([0-9.]+)", emission_line)
            self.assertEqual(len(values), 3)
            vals = [float(v) for v in values]
            self.assertTrue(all(.05 < v < .15 for v in vals), vals)
        source = CS.split("internal static GameObject CreatePlane(", 1)[1].split(
            "internal static GameObject CreateElongatedCanopy(", 1)[0]
        self.assertNotIn("AddComponent<Light>", source)
        self.assertNotIn("new Material(", source)
        helper = CS.split("private static Material EnsureDaylightFill(", 1)[1].split(
            "private static GameObject Part(", 1)[0]
        self.assertIn("new Material(source)", helper)
        self.assertIn('cached.EnableKeyword("_EMISSION")', helper)
        self.assertIn('cached.SetColor("_EmissionColor", fill)', helper)
        self.assertIn("if (cached != null) return cached;", helper)
        self.assertIn("ref hullDaylightFinish", source)
        self.assertIn("ref wingDaylightFinish", source)
        self.assertNotIn("source.EnableKeyword", helper)


if __name__ == "__main__":
    unittest.main()
