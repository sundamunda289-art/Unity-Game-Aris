#!/usr/bin/env python3
"""Static Android facade admission contract; not a Unity/handset benchmark."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / "Assets/AarisWorld/Scripts/AarisSettlementExpansion.cs").read_text()
GATE = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileStreamingFrameGate.cs").read_text()
TRIM = (ROOT / "Assets/AarisWorld/Scripts/AarisHouseFacadeTrim.cs").read_text()


class FacadeAdmission(unittest.TestCase):
    def setUp(self):
        self.body = SRC.split("private IEnumerator PlaceSitesAcrossFrames(", 1)[1].split(
            "private bool TryRelocateInteractiveSite(", 1)[0]

    def test_original_house_visible_before_optional_trim(self):
        body = self.body
        created = body.index("GameObject instance = site.interactive")
        registered = body.index("placed.Add(instance);")
        wait = body.index("yield return null;", registered)
        extra_slot = body.index("!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()", registered)
        cosmetic = body.index("AarisHouseFacadeTrim.Install(instance", registered)
        self.assertLess(created, registered)
        self.assertLess(registered, wait)
        self.assertLess(wait, extra_slot)
        self.assertLess(extra_slot, cosmetic)
        self.assertIn("if (!isActiveAndEnabled || instance == null) yield break;", body)

    def test_android_only_gate_and_unmodified_visuals(self):
        body = self.body
        registered = body.index("placed.Add(instance);")
        cosmetic = body.index("AarisHouseFacadeTrim.Install(instance", registered)
        phase = body[registered:cosmetic]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", phase)
        self.assertIn("#endif", phase)
        self.assertIn("site.prefabKey == \"cottage\"", phase)
        self.assertIn("site.prefabKey == \"bungalow\"", phase)
        self.assertIn("site.prefabKey == \"detached\"", phase)
        self.assertIn("while (isActiveAndEnabled && instance != null &&", phase)
        self.assertIn("TryClaimOptionalModelBuild()", phase)
        self.assertIn("claimedFrame = frame;", GATE)
        self.assertIn("mesh.UploadMeshData(true);", TRIM)
        self.assertIn("renderer.sharedMaterial = sharedStucco;", TRIM)
        self.assertIn("lod.RecalculateBounds();", TRIM)
        self.assertNotIn("QualitySettings.", phase)
        self.assertNotIn("renderScale =", phase)
        self.assertNotIn("Destroy(instance)", phase)


if __name__ == "__main__":
    unittest.main(verbosity=2)
