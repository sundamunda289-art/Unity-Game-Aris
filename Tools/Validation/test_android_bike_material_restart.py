#!/usr/bin/env python3
"""Android streamed-bike URP material restart source invariants."""
import unittest
from pathlib import Path
FILE=Path(__file__).resolve().parents[2]/"Assets/AarisWorld/Scripts/AarisRoadsideBikeScenery.cs"
BIKE=FILE.read_text()
class AndroidBikePaletteRestartTests(unittest.TestCase):
    def test_original_prewarm_tracks_staged_coroutine(self):
        self.assertIn("bikeMaterialBuild = StartCoroutine(BuildMaterialsAcrossFrames(true));",BIKE)
        self.assertIn("if (!bikeMaterialsReady) return;",BIKE)
    def test_disable_stops_partial_and_preserves_ready_bikes(self):
        p=BIKE.split("private void OnDisable()",1)[1].split("private System.Collections.IEnumerator ResumeMaterialPaletteAfterEnable()",1)[0]
        self.assertIn("StopCoroutine(bikeMaterialBuild);",p)
        self.assertIn("bikeMaterialBuild = null;",p)
        self.assertIn("if (!bikeMaterialsReady) ReleaseIncompleteMaterialPalette();",p)
    def test_reenable_restarts_after_admitted_frame(self):
        s=BIKE.split("private void OnEnable()",1)[1].split("private void OnDisable()",1)[0]
        self.assertIn("if (bikeSource != null && !bikeMaterialsReady",s)
        self.assertIn("StartCoroutine(ResumeMaterialPaletteAfterEnable());",s)
        p=BIKE.split("private System.Collections.IEnumerator ResumeMaterialPaletteAfterEnable()",1)[1].split("private void ReleaseIncompleteMaterialPalette()",1)[0]
        self.assertLess(p.index("yield return null;"),p.index("TryClaimOptionalModelBuild()"))
        self.assertLess(p.index("TryClaimOptionalModelBuild()"),p.index("yield return BuildMaterialsAcrossFrames(true);"))
    def test_destroy_does_not_double_release_paint_alias(self):
        p=BIKE.split("private void ReleaseIncompleteMaterialPalette()",1)[1].split("private void OnLowMemory()",1)[0]
        self.assertIn("for (int i = 1; i < paintVariants.Length; i++)",p)
        self.assertIn("paintVariants[0] = null;",p)
        self.assertIn("for (int i = 0; i < surfaces.Length; i++)",p)
        self.assertIn("surfaces[i] = null;",p)
    def test_visual_shape_and_distance_constraints_untouched(self):
        for token in ("private const int MaximumVisibleBikes = 3;",
                      "private const float LoadRadius = 45f;",
                      "private const float UnloadRadius = 66f;",
                      "private const float PrewarmRadius = 110f;",
                      "ReuseBike(parkedBikePool.Pop(), nearest.point, spot)"):
            self.assertIn(token,BIKE)
if __name__=="__main__":unittest.main()
