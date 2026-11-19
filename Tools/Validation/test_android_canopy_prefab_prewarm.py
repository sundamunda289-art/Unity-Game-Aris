#!/usr/bin/env python3
"""Android visual-preserving parachute prefab prewarm source regression."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
VISUAL = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineDropVisuals.cs").read_text()
DROP = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineDrop.cs").read_text()


class CanopyPrewarm(unittest.TestCase):
    def test_aircraft_first_frame_is_not_blocked_by_preload(self):
        start = DROP.split("private void Start()", 1)[1].split(
            "private void BuildFlightPath()", 1)[0]
        self.assertIn('phase = DropPhase.InPlane;', start)
        self.assertIn("StartCoroutine(AarisOfflineDropVisuals.PrewarmCanopyPrefab());", start)
        self.assertLess(start.index('hud.SetAction("EJECT", true);'),
                        start.index('StartCoroutine(AarisOfflineDropVisuals.PrewarmCanopyPrefab());'))
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", start)

    def test_async_prefab_request_runs_after_yield_on_android(self):
        section = VISUAL.split("internal static IEnumerator PrewarmCanopyPrefab()", 1)[1].split(
            "#endif", 1)[0]
        self.assertIn("yield return null;", section)
        self.assertIn('Resources.LoadAsync<GameObject>(\n                "AarisDrop/ElongatedParachute")', section)
        self.assertIn("request.priority = -1;", section)
        self.assertIn("yield return request;", section)
        self.assertIn("prewarmedCanopyPrefab = request.asset as GameObject;", section)
        self.assertLess(section.index("yield return null;"),
                        section.index("Resources.LoadAsync<GameObject>"))
        self.assertNotIn("Instantiate(", section)

    def test_original_art_and_early_open_fallback_preserved(self):
        body = VISUAL.split("internal static GameObject CreateElongatedCanopy(", 1)[1].split(
            "private static Vector3 CanopyPoint(", 1)[0]
        self.assertIn("prewarmedCanopyPrefab != null", body)
        self.assertEqual(body.count('Resources.Load<GameObject>("AarisDrop/ElongatedParachute")'), 2)
        self.assertIn("var model = UnityEngine.Object.Instantiate(imported, rig, false);", body)
        self.assertIn('model.name = "LocalCanopyArt";', body)
        self.assertIn("else\n            {\n                Material[] paints", body)
        self.assertIn("RigidLine(rig,", body)
        self.assertIn("if (phase != DropPhase.Freefall || Time.time - airborneSince < 0.35f) return;", DROP)
        self.assertIn("canopy = AarisOfflineDropVisuals.CreateElongatedCanopy(transform);", DROP)

    def test_static_prefab_reference_resets_on_subsystem_reload(self):
        self.assertIn("[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]", VISUAL)
        self.assertIn("private static void ResetCanopyPrewarm()", VISUAL)
        self.assertIn("prewarmedCanopyPrefab = null;", VISUAL)

    def test_imported_asset_untouched(self):
        obj = ROOT / "Assets/Resources/AarisDrop/ElongatedParachute.obj"
        meta = obj.with_suffix(".obj.meta")
        self.assertTrue(obj.is_file())
        self.assertTrue(meta.is_file())
        self.assertGreater(obj.stat().st_size, 30_000)
        self.assertLess(obj.stat().st_size, 1_000_000)
        self.assertIn("CreatePlane(pathStart", DROP)


if __name__ == "__main__":
    unittest.main(verbosity=2)
