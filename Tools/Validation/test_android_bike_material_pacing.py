#!/usr/bin/env python3
"""Roadside motorcycle URP material staging: source contracts, not handset FPS."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
BIKE = (ROOT / "Assets/AarisWorld/Scripts/AarisRoadsideBikeScenery.cs").read_text(encoding="utf-8")
GATE = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileStreamingFrameGate.cs").read_text(encoding="utf-8")


class BikeMaterialPacingContract(unittest.TestCase):
    def test_stages_only_budget_android_without_changing_editor_path(self):
        update = BIKE.split("private void Update()", 1)[1].split("private GameObject ReuseBike(", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", update)
        self.assertIn("if (AarisMobileStreamingFrameGate.IsConstrainedDevice())", update)
        self.assertIn("StartCoroutine(BuildMaterialsAcrossFrames(true));", update)
        self.assertIn("else\n                    SetupMaterials();", update)
        self.assertIn("#else\n                SetupMaterials();\n#endif", update)
        self.assertIn("if (!enabled || !bikeMaterialsReady) return;", update)
        self.assertIn("if (!bikeMaterialsReady) return;", update)
        self.assertIn("while (job.MoveNext())", BIKE)

    def test_partial_palette_never_instantiates_bike(self):
        update = BIKE.split("private void Update()", 1)[1].split("private GameObject ReuseBike(", 1)[0]
        self.assertLess(update.index("if (!bikeMaterialsReady) return;"),
                        update.index("CreateBike(nearest.point, spot)"))
        self.assertEqual(BIKE.count("bikeMaterialsReady = true;"), 1)
        self.assertLess(BIKE.index("paintVariants[i].SetColor(\"_BaseColor\", paints[i]);"),
                        BIKE.index("bikeMaterialsReady = true;"))

    def test_native_material_work_is_spread_and_admitted(self):
        setup = BIKE.split("private System.Collections.IEnumerator BuildMaterialsAcrossFrames(bool stagger)", 1)[1].split(
            "private void OnEnable()", 1)[0]
        self.assertIn("if (stagger && i > 0 && (i & 1) == 0)", setup)
        self.assertEqual(setup.count("yield return null;\n                    while (isActiveAndEnabled"), 1)
        self.assertEqual(setup.count("AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()"), 3)
        self.assertIn("if (!isActiveAndEnabled) yield break;", setup)
        self.assertIn("if (IsAssetCleanupPending())", GATE)
        self.assertIn("if (claimedFrame == frame)", GATE)
        # The sequence is a one-time setup; no allocations in per-frame hot loop.
        self.assertLess(BIKE.index("private void SetupMaterials()"), BIKE.index("private void Update()"))

    def test_first_pair_waits_for_a_new_admitted_frame(self):
        setup = BIKE.split("private System.Collections.IEnumerator BuildMaterialsAcrossFrames(bool stagger)", 1)[1]
        self.assertLess(setup.index("if (stagger)"),
                        setup.index('Shader shader = Shader.Find("Universal Render Pipeline/Lit");'))
        first_gate = setup.split("if (stagger)", 1)[1].split("Shader shader", 1)[0]
        self.assertIn("yield return null;", first_gate)
        self.assertIn("AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()", first_gate)
        self.assertIn("if (!isActiveAndEnabled) yield break;", first_gate)

    def test_interrupted_palette_rebuilds_after_gameobject_reenable(self):
        # Unity stops a coroutine when its GameObject is deactivated. A partial
        # palette must not leave bikeSource cached and bikes invisible forever.
        disable = BIKE.split("private void OnDisable()", 1)[1].split(
            "private void Update()", 1)[0]
        update = BIKE.split("private void Update()", 1)[1].split(
            "private GameObject ReuseBike(", 1)[0]
        self.assertIn("private Coroutine bikeMaterialsBuild;", BIKE)
        self.assertIn("StopCoroutine(bikeMaterialsBuild);", disable)
        self.assertIn("bikeMaterialsBuild = null;", disable)
        self.assertIn("if (bikeMaterialsReady) return;", disable)
        self.assertIn("Destroy(paintVariants[i]);", disable)
        self.assertIn("paintVariants[0] = null;", disable)
        self.assertIn("Destroy(surfaces[i]);", disable)
        self.assertIn("surfaces[i] = null;", disable)
        self.assertIn("!bikeMaterialsReady && bikeSource != null", update)
        self.assertIn("bikeMaterialsBuild == null", update)
        self.assertIn("AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())", update)
        self.assertIn("bikeMaterialsBuild = StartCoroutine(BuildMaterialsAcrossFrames(true));", update)
        self.assertLess(update.rindex("bikeMaterialsBuild = StartCoroutine(BuildMaterialsAcrossFrames(true));"),
                        update.index("if (!bikeMaterialsReady) return;"))
        self.assertIn("bikeMaterialsBuild = null;", update)

    def test_original_mesh_visuals_paint_limits_and_cleanup_remain(self):
        invariants = (
            "AarisVehicles/Authored/AarisRoadBike",
            "private const int MaximumVisibleBikes = 3;",
            "private const float LoadRadius = 45f;",
            "private const float PrewarmRadius = 110f;",
            "private const float UnloadRadius = 66f;",
            "new Color(.36f,.054f,.062f)",
            'surfaces[0].EnableKeyword("_CLEARCOAT");',
            'surfaces[i].EnableKeyword("_EMISSION");',
            "Instantiate(bikeSource, root.transform, false);",
            "ReuseBike(parkedBikePool.Pop(), nearest.point, spot)",
            "Destroy(paintVariants[i]);",
        )
        for token in invariants:
            self.assertIn(token, BIKE)
        # No material cloning for every visible vehicle and no altered art.
        self.assertEqual(BIKE.count("new Material(shader)"), 1)
        self.assertEqual(BIKE.count("new Material(surfaces[0])"), 1)
        self.assertNotIn("QualitySettings.masterTextureLimit =", BIKE)
        self.assertNotIn("renderScale =", BIKE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
