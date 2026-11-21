#!/usr/bin/env python3
"""Source contracts for direct essential weapon imports and optional frame fencing.

Not a Unity Editor build or on-device FPS measurement.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text()
WEAPONS = (SRC / "AarisWeaponCatalog.cs").read_text()
HARNESS = (ROOT / "Tools/Validation/StreamingAdmissionTests.cs").read_text()


class EssentialWeaponImportFence(unittest.TestCase):
    def test_direct_weapon_catalog_and_prefab_loads_never_wait_for_optional_gate(self):
        body = WEAPONS.split("internal static IEnumerator LoadForMatchAsync()", 1)[1].split(
            "public static GameObject ModelFor(", 1
        )[0]
        self.assertIn('Resources.LoadAsync<TextAsset>(\n                "AarisWeapons/weapon_catalog")', body)
        self.assertIn("definitionsRequest.priority = 0;", body)
        self.assertIn("AarisMobileStreamingFrameGate.TrackEssentialWeaponLoad(definitionsRequest);", body)
        self.assertIn("Resources.LoadAsync<GameObject>(spec.modelResource)", body)
        self.assertIn("AarisMobileStreamingFrameGate.TrackEssentialWeaponLoad(request);", body)
        self.assertEqual(body.count("TrackEssentialWeaponLoad("), 2)
        self.assertNotIn("TryStartOptionalLoadAsync", body)
        self.assertNotIn("WaitForCompletion(", body)
        self.assertLess(body.index("TrackEssentialWeaponLoad(definitionsRequest);"),
                        body.index("yield return definitionsRequest;"))
        self.assertLess(body.index("TrackEssentialWeaponLoad(request);"),
                        body.index("yield return request;"))
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", body)

    def test_budget_gpu_gate_fences_model_import_and_diagnostics(self):
        self.assertIn("internal static void TrackEssentialWeaponLoad(ResourceRequest request)", GATE)
        self.assertIn("private static bool IsEssentialWeaponImportBusy()", GATE)
        self.assertIn("if (!essentialWeaponLoadInFlight.isDone)", GATE)
        self.assertIn("essentialWeaponCompletionFrame = Time.frameCount;", GATE)
        self.assertEqual(GATE.count("bool essentialWeaponBusy = IsEssentialWeaponImportBusy();"), 2)
        self.assertEqual(GATE.count("if (essentialAudioBusy || essentialWeaponBusy || essentialLootBusy) return false;"), 2)
        self.assertIn("essentialWeaponLoadInFlight = null;", GATE)
        self.assertIn("essentialWeaponCompletionFrame = -1;", GATE)
        self.assertIn("EssentialWeaponDeferredAttempts = 0;", GATE)
        self.assertNotIn("QualitySettings.", GATE)
        self.assertNotIn("TerrainData.", GATE)

    def test_compiled_fixture_exercises_real_gate_with_overlapping_audio(self):
        self.assertIn("public class TextAsset : Object { }", HARNESS)
        self.assertIn("private static void EssentialWeaponFence()", HARNESS)
        self.assertIn("TrackEssentialAudioLoad(audio);", HARNESS)
        self.assertIn("TrackEssentialWeaponLoad(weapon);", HARNESS)
        self.assertIn("Configure(8192, \"Adreno 730\");", HARNESS)
        self.assertIn("DiagnosticImportFence(); EssentialWeaponFence(); EssentialLootAndJsonParseFence(); Pressure();", HARNESS)


if __name__ == "__main__":
    unittest.main(verbosity=2)
