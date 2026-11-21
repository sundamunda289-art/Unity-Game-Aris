#!/usr/bin/env python3
"""Regression: native import gate must inspect current floor-loot resources."""
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CS = ROOT / "Assets/AarisWorld/Editor/AarisWeaponImportValidation.cs"
CAT = ROOT / "Assets/Resources/AarisWeapons/weapon_catalog.json"
LOOT = ROOT / "Assets/Resources/AarisWeapons/loot_candidates.json"

class LiveGroundLootImport(unittest.TestCase):
    def test_editor_uses_current_catalog_not_legacy_models(self):
        s = CS.read_text()
        self.assertIn('Resources.Load<GameObject>(spec.modelResource)', s)
        self.assertIn('AssetImporter.GetAtPath(path) as ModelImporter', s)
        self.assertIn('renderer', s)
        self.assertIn('r.enabled && r.gameObject.activeSelf', s)
        self.assertIn('AarisWeapons/loot_candidates', s)
        self.assertNotIn('string basePath = "Assets/Resources/AarisWeapons/Models/"', s)
        self.assertNotIn('parsed.weapons.Length != 5', s)
        self.assertIn('importedModels.Add(spec.modelResource)', s)
        self.assertIn('if (!seenIds.Add(spec.id))', s)

    def test_all_live_model_files_exist_for_authored_loot(self):
        models = json.loads(CAT.read_text())['weapons']
        # Five distinct meshes now back six playable gun variants (DMR shares BattleRifle).
        self.assertGreaterEqual(len(models), 6)
        self.assertGreaterEqual(len({model['modelResource'] for model in models}), 5)
        self.assertEqual(len({model['id'] for model in models}), len(models))
        self.assertEqual({0, 1, 2, 4}, {model['scopePower'] for model in models})
        self.assertIn('DMR', {model['id'] for model in models})
        for model in models:
            p = ROOT / 'Assets/Resources' / model['modelResource']
            self.assertTrue(p.with_suffix('.fbx').is_file()
                            or p.with_suffix('.obj').is_file(), str(p))
        data = json.loads(LOOT.read_text())
        self.assertEqual(data['schema'], 1)
        self.assertEqual(data['sizeMetres'], 2000)
        self.assertGreaterEqual(len(data['candidates']), 500)
        self.assertTrue(any(p['kind'] == 'open' for p in data['candidates']))
        self.assertTrue(any(p['kind'] == 'indoor' for p in data['candidates']))

if __name__ == '__main__':
    unittest.main()
