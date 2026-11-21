#!/usr/bin/env python3
"""Source-level regression for async Android aircraft startup, not Android FPS."""
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
DROP = (SCRIPTS / "AarisOfflineDrop.cs").read_text(encoding="utf-8")
VIS = (SCRIPTS / "AarisOfflineDropVisuals.cs").read_text(encoding="utf-8")

def section(src, begin, end):
    return src.split(begin, 1)[1].split(end, 1)[0]

class AndroidPlaneStartupRegression(unittest.TestCase):
    def test_start_preserves_editor_path_and_suspends_input_before_async(self):
        start = section(DROP, "private void Start()", "private IEnumerator LoadPlaneAndBeginFlight()")
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", start)
        self.assertIn("StartCoroutine(LoadPlaneAndBeginFlight());", start)
        self.assertIn("AarisOfflineDropVisuals.CreatePlane(pathStart,", start)
        self.assertLess(start.index("controlsSuspended = true;"), start.index("StartCoroutine(LoadPlaneAndBeginFlight());"))
        self.assertLess(start.index("BuildFlightPath();"), start.index("StartCoroutine(LoadPlaneAndBeginFlight());"))

    def test_authored_high_detail_first_and_async_fallback(self):
        load = section(DROP, "private IEnumerator LoadPlaneAndBeginFlight()", "private void CompleteFlightSetup()")
        self.assertIn('Resources.LoadAsync<GameObject>(\n                "AarisDrop/CargoPlaneRealistic")', load)
        self.assertIn('Resources.LoadAsync<GameObject>("AarisDrop/CargoPlane")', load)
        self.assertEqual(load.count("request.priority = -1;"), 2)
        self.assertEqual(load.count("yield return request;"), 2)
        self.assertIn("if (!isActiveAndEnabled) yield break;", load)
        self.assertNotIn("Resources.Load<GameObject>", load)
        self.assertIn("out propellers, source, highDetail, true", load)
        self.assertLess(load.index("yield return request;"), load.index("CompleteFlightSetup();"))

    def test_essential_aircraft_frame_reserved_before_native_integration(self):
        load = section(DROP, "private IEnumerator LoadPlaneAndBeginFlight()",
                       "private void CompleteFlightSetup()")
        marker = "AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialAircraft();"
        self.assertEqual(load.count(marker), 1)
        # Existing single-frame separation must remain in front of the real
        # authored aircraft creation, with no extra coroutine startup delay.
        self.assertLess(load.index("PrewarmAircraftFinishes()"), load.index(marker))
        self.assertLess(load.index(marker), load.index("yield return null;", load.index(marker)))
        self.assertLess(load.index("yield return null;", load.index(marker)),
                        load.index("AarisOfflineDropVisuals.CreatePlane("))
        gate = (SCRIPTS / "AarisMobileStreamingFrameGate.cs").read_text()
        # The native fence now retains both due frames when the same aircraft
        # re-reserves during the frame it was already due. Test semantics,
        # not the former single-slot assignment's exact source spelling.
        self.assertIn("int nextFrame = Time.frameCount + 1;", gate)
        self.assertIn("essentialAircraftFrame = nextFrame;", gate)
        self.assertIn("previousEssentialAircraftFrame = essentialAircraftFrame;", gate)
        self.assertIn("Time.frameCount == previousEssentialAircraftFrame", gate)
        self.assertIn("Time.frameCount == essentialAircraftFrame", gate)
        self.assertIn("essentialAircraftFrame = -1;", gate)
        self.assertIn("IsConstrainedDevice()", gate)

    def test_flight_cannot_run_until_model_ready(self):
        self.assertIn("private bool planeReady;", DROP)
        self.assertIn("if (!planeReady || phase == DropPhase.Landed || view == null) return;", DROP)
        self.assertIn("if (!planeReady || view == null || phase == DropPhase.Landed) return;", DROP)
        finish = section(DROP, "private void CompleteFlightSetup()", "private void BuildFlightPath()")
        for preserved in ("capsule.enabled = false;", "HidePlayer();", "AarisOfflineDropHud.Create(this)",
                          "AarisOfflineDropVisuals.PrewarmCanopyPrefab()", "GetPlaneCameraPose(out Vector3 initialPosition,"):
            self.assertIn(preserved, finish)
        self.assertLess(finish.index("previousMouse = Input.mousePosition;"), finish.index("planeReady = true;"))

    def test_original_plane_assets_and_material_path_untouched(self):
        create = section(VIS, "internal static GameObject CreatePlane(", "/// <summary>")
        for preserved in ('Resources.Load<GameObject>("AarisDrop/CargoPlaneRealistic")',
                          'Resources.Load<GameObject>("AarisDrop/CargoPlane")',
                          "UnityEngine.Object.Instantiate(prefab, position, rotation)",
                          "SavedAircraftFinish(", "AddAircraftMarkerLights(root.transform);"):
            self.assertIn(preserved, create)
        self.assertIn("GameObject preloadedPrefab = null", create)
        self.assertIn("else highDetailTwin = preloadedHighDetail && prefab != null;", create)
        self.assertIn("PlaneModelResolveMs = preloadedResolved ? -1f :", create)

if __name__ == "__main__":
    unittest.main(verbosity=2)
