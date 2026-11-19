#!/usr/bin/env python3
"""Android shared loot JSON load is async and does not re-enter sync load on land.

Static contract checks + deterministic two-consumer scheduling. These tests
cannot measure Unity ResourceRequest import time or Android frame pacing.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
WORLD = (ROOT / "Assets/AarisWorld/Scripts/AarisWeaponLootWorld.cs").read_text()
HUD = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineTacticalHUD.cs").read_text()


def simulate(shared_load_finished_at, first_ground_frame, end_frame=20):
    medical_ready = False
    gun_ready = False
    medical_sites_scanned = 0
    for frame in range(1, end_frame + 1):
        warmed = frame >= shared_load_finished_at
        if frame >= first_ground_frame and not medical_ready and warmed:
            medical_ready = True
            medical_sites_scanned += 1
        if warmed and not gun_ready:
            gun_ready = True
    return medical_ready, gun_ready, medical_sites_scanned


class SharedLootCandidateImport(unittest.TestCase):
    def test_shared_one_time_async_resources_import_and_parse(self):
        doc = WORLD.split("internal sealed class AarisLootDocument", 1)[1].split(
            "public sealed class AarisWeaponLootWorld", 1)[0]
        warm = doc.split("internal static IEnumerator LoadForAndroidAsync()", 1)[1].split(
            "#endif", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", doc)
        self.assertIn("ResourceRequest request = Resources.LoadAsync<TextAsset>(", warm)
        self.assertIn('"AarisWeapons/loot_candidates"', warm)
        self.assertIn("request.priority = -1;", warm)
        self.assertLess(warm.index("yield return request;"),
                        warm.index("yield return null;"))
        self.assertLess(warm.index("yield return null;"),
                        warm.index("JsonUtility.FromJson<AarisLootDocument>"))
        self.assertIn("AndroidWarmupFinished = true;", warm)
        self.assertIn("internal static AarisLootDocument AndroidCached => cached;", doc)
        self.assertIn("AndroidWarmupFinished = false;", doc)
        self.assertIn("cached = null;", doc)
        self.assertIn('Resources.Load<TextAsset>("AarisWeapons/loot_candidates")', doc)

    def test_tactical_scan_does_not_force_android_sync_load(self):
        medical = HUD.split("private void LoadMedicalSites()", 1)[1].split(
            "private void StartHealing()", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", medical)
        self.assertIn("var doc = AarisLootDocument.AndroidCached;", medical)
        self.assertIn("#else\n                var doc = AarisLootDocument.Load();", medical)
        stream = HUD.split("private void StreamMedicalSites()", 1)[1].split(
            "private void RecycleMedicalCase(", 1)[0]
        self.assertLess(stream.index("if (!AarisLootDocument.AndroidWarmupFinished) return;"),
                        stream.index("medicalSitesLoaded = true;"))
        self.assertLess(stream.index("medicalSitesLoaded = true;"),
                        stream.index("LoadMedicalSites();"))
        self.assertIn("if (medicalSites.Count == 0) return;", stream)
        self.assertIn("if (site.taken || site.visual != null || now < site.retry) continue;", stream)

    def test_android_gun_world_never_falls_back_to_sync_document_load(self):
        init_android = WORLD.split("private IEnumerator InitializeAndroid()", 1)[1].split(
            "private void Initialize()", 1)[0]
        self.assertIn("yield return AarisLootDocument.LoadForAndroidAsync();", init_android)
        self.assertLess(init_android.index("LoadForAndroidAsync()"),
                        init_android.index("LoadForMatchAsync()"))
        self.assertLess(init_android.index("LoadForMatchAsync()"),
                        init_android.index("Initialize();"))
        self.assertEqual(init_android.count("if (!isActiveAndEnabled) yield break;"), 2)
        init = WORLD.split("private void Initialize()", 1)[1].split(
            "private IEnumerator CompleteAndroidInitialization", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", init)
        self.assertIn("doc = AarisLootDocument.AndroidCached;", init)
        self.assertIn("#else\n            try { doc = AarisLootDocument.Load(); }", init)
        self.assertIn("if (doc == null || doc.schema != 1 || doc.sizeMetres != 2000 ||", init)
        self.assertIn("enabled = false;", init)

    def test_landing_before_or_after_manifest_completion_preserves_sites(self):
        for load_frame in (2, 4, 8, 15):
            for landing_frame in (1, 2, 5, 12, 17):
                medical, guns, scans = simulate(load_frame, landing_frame)
                self.assertTrue(medical)
                self.assertTrue(guns)
                self.assertEqual(scans, 1)

    def test_bad_asset_must_fail_closed_without_reimport_loop(self):
        doc = WORLD.split("internal sealed class AarisLootDocument", 1)[1].split(
            "public sealed class AarisWeaponLootWorld", 1)[0]
        self.assertIn("TextAsset source = request.asset as TextAsset;", doc)
        self.assertIn('Debug.LogError("Aaris: missing embedded AarisWeapons/loot_candidates.json.");', doc)
        self.assertIn("catch (Exception e)", doc)
        self.assertIn("AndroidWarmupFinished = true;", doc)
        self.assertIn("if (doc == null || doc.schema != 1", WORLD)
        self.assertNotIn("Resources.Load<", HUD.split("private void LoadMedicalSites()", 1)[1].split(
            "private void StartHealing()", 1)[0].split("#if UNITY_ANDROID && !UNITY_EDITOR",1)[1].split("#else",1)[0])


if __name__ == "__main__":
    unittest.main(verbosity=2)
