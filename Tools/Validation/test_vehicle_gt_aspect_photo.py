#!/usr/bin/env python3
"""Guard source-authored GT geometry and all-eight catalog-scale screenshots."""
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs").read_text()
PHOTO = (ROOT / "Assets/AarisWorld/Editor/AarisVehiclePhotoStudio.cs").read_text()
CATALOG = json.loads((ROOT / "Assets/Resources/AarisVehicles/vehicle_catalog.json").read_text())
LAYOUT = json.loads((ROOT / "Docs/MAP_LAYOUT.json").read_text())

class BalancedGtAndFleet(unittest.TestCase):
    def test_actual_map_widths_and_eight_catalog_classes(self):
        self.assertEqual(LAYOUT["size_m"], [2000, 2000])
        self.assertEqual(sorted({road["width"] for road in LAYOUT["roads"]}), [7, 8])
        names = {item["id"] for item in CATALOG["vehicles"]}
        self.assertEqual(names, {"Sedan", "SUV", "Sports", "GT", "Hatchback",
                                 "Van", "Utility", "Buggy"})
        for item in CATALOG["vehicles"]:
            self.assertGreater(item["referenceWidthMetres"], 1.65)
            self.assertLessEqual(item["referenceWidthMetres"], 2.10)
            self.assertLess(item["referenceLengthMetres"], 5.50)

    def test_gt_corrects_short_roof_without_elliptic_tyre_roll_plane(self):
        build = WORLD.split("private bool Build(", 1)[1].split("private void Update()", 1)[0]
        self.assertIn('record.type.id == "GT"', build)
        self.assertIn("record.type.useEmbeddedAuthoredWheels", build)
        self.assertIn("new Vector3(lateral, rolling, rolling)", build)
        self.assertIn("widthMetres / bounds.size.x", build)
        self.assertIn("lengthMetres / bounds.size.z", build)
        self.assertIn("mesh.transform.localScale *= Mathf.Clamp(scale, .01f, 50f)", build)
        self.assertIn("AlignEmbeddedTyresToRoad(root.transform, mesh.transform)", build)
        self.assertIn("motor.FitShellAndWheelbase(bounds)", build)
        gt = next(i for i in CATALOG["vehicles"] if i["id"] == "GT")
        self.assertEqual(gt["authoredModelResource"],
                         "AarisVehicles/PremiumRoadGT/AarisRoadGT")
        source_x, source_y, source_z = 2.486, 1.189, 4.631
        lateral = gt["referenceWidthMetres"] / source_x
        rolling = gt["referenceLengthMetres"] / source_z
        self.assertLess(lateral, rolling)
        self.assertAlmostEqual(source_x * lateral, 2.08, places=4)
        self.assertAlmostEqual(source_z * rolling, 4.48, places=4)
        self.assertGreater(source_y * rolling, 1.10)
        self.assertLess(source_y * min(lateral,rolling), 1.05)

    def test_native_photo_review_tracks_catalogue_not_old_hardcoded_sedan_scale(self):
        self.assertIn("JsonUtility.FromJson<AarisVehicleStudioCatalog>", PHOTO)
        self.assertIn("type.referenceWidthMetres", PHOTO)
        self.assertIn("type.referenceLengthMetres", PHOTO)
        self.assertIn("new Vector3(transverse, wheelPlane, wheelPlane)", PHOTO)
        self.assertIn("Assets/Resources/AarisVehicles/PremiumRoadGT/AarisRoadGT.prefab", PHOTO)
        self.assertIn("Assets/Resources/AarisVehicles/Authored/AarisBuggy.obj", PHOTO)
        self.assertIn("new Vector3(.7f, 1f, 1.8f)", PHOTO)
        self.assertNotIn("1.95f / Mathf.Max", PHOTO)
        self.assertIn("AARIS_VEHICLE_STUDIO_PASS", PHOTO)

    def test_sedan_wheel_review_uses_actual_game_materials(self):
        self.assertIn("Studio Sedan Wheels", PHOTO)
        self.assertIn('"AarisVehicles/Models/colormap"', PHOTO)
        self.assertIn("AttachWheelModels(", PHOTO)
        self.assertIn('"AarisVehicles/Models/wheel-default"', PHOTO)
        self.assertIn('studioWheelMat.SetFloat("_Smoothness", .18f)', PHOTO)
        self.assertNotIn("new Material(shader) { name = \"Temporary Sedan Wheel\" }", PHOTO)

if __name__ == "__main__":
    unittest.main()
